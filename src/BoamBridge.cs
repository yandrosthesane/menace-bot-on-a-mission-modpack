using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using HarmonyLib;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;
using Il2CppMenace.Tactical.AI.Behaviors;
using Il2CppMenace.Tactical.AI.Data;
using Il2CppMenace.Tactical.Skills;
using Il2CppMenace.States;
using Il2CppMenace.Tools;
using MelonLoader;
using Menace.ModpackLoader;
using Menace.SDK;

namespace BOAM;

/// <summary>
/// Thin C# bridge plugin — calls the BOAM Tactical Engine over HTTP
/// at game event checkpoints and applies the returned modifications.
/// </summary>
public class BoamBridge : IModpackPlugin
{
    public static BoamBridge Instance { get; private set; }
    public static MelonLogger.Instance Logger { get; private set; }

    private HarmonyLib.Harmony _harmony;
    private volatile bool _engineAvailable;

    // Feature flags — populated from engine /status response
    public bool CriterionLogging { get; private set; }
    public bool HeatmapsEnabled { get; private set; }
    public bool ActionLoggingEnabled { get; private set; }
    public bool AiLoggingEnabled { get; private set; }
    private bool _inTactical;
    private int _initDelay;
    private bool _ready;

    /// <summary>Tactical scene is loaded and initialized (minimap, unit registry).</summary>
    public bool IsTacticalReady => _ready;

    /// <summary>Tactical scene ready AND engine is connected (for events that POST to the engine).</summary>
    public bool IsEngineReady => _ready && _engineAvailable;
    private QueryCommandServer _commandServer;
    private readonly System.Collections.Concurrent.ConcurrentQueue<BridgeServer.ActionCommand> _executeQueue = new();
    private float _nextCommandTime;
    private TacticalMap.TacticalMapOverlay _tacticalMap;



    public void OnInitialize(MelonLogger.Instance logger, HarmonyLib.Harmony harmony)
    {
        Instance = this;
        Logger = logger;
        _harmony = harmony;

        // Apply attribute-based patches
        _harmony.PatchAll(typeof(BoamBridge).Assembly);

        // Register manual patches per data event
        GameEvents.OnTurnEndEvent.Register(harmony, Logger);
        GameEvents.ActorChangedEvent.Register(harmony, Logger);
        GameEvents.PreviewReadyEvent.Register(harmony, Logger);
        GameEvents.ActionLoggingEvent.Register(harmony, Logger);
        GameEvents.CombatLoggingEvent.Register(harmony, Logger);

        // Start command server (symmetric protocol: /query + /command)
        _commandServer = new QueryCommandServer(Logger, BridgeServer.Port);
        // Register old command handlers as command types
        _commandServer.AddCommandHandler("tile-modifier", root => {
            TileModifierStore.SetFromJson(root.GetRawText());
            return "{\"status\":\"ok\"}";
        });
        _commandServer.AddCommandHandler("tile-modifier-batch", root => {
            TileModifierStore.Clear();
            if (root.TryGetProperty("actors", out var actors))
            {
                foreach (var actor in actors.EnumerateArray())
                    TileModifierStore.SetFromJson(actor.GetRawText());
            }
            return "{\"status\":\"ok\"}";
        });
        _commandServer.AddCommandHandler("tile-modifier-clear", _ => {
            TileModifierStore.Clear();
            return "{\"status\":\"cleared\"}";
        });
        _commandServer.AddCommandHandler("tile-modifier-ready", _ => {
            TileModifierStore.SetReady();
            return "{\"status\":\"ready\"}";
        });
        _commandServer.AddCommandHandler("execute", root => {
            var action = root.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
            var x = root.TryGetProperty("x", out var xv) ? xv.GetInt32() : 0;
            var z = root.TryGetProperty("z", out var zv) ? zv.GetInt32() : 0;
            var skill = root.TryGetProperty("skill", out var sv) ? sv.GetString() ?? "" : "";
            var actor = root.TryGetProperty("actor", out var av) ? av.GetString() ?? "" : "";
            var delayMs = root.TryGetProperty("delay_ms", out var dv) ? dv.GetInt32() : 0;
            _executeQueue.Enqueue(new BridgeServer.ActionCommand {
                Action = action, X = x, Z = z, Skill = skill, Actor = actor, DelayMs = delayMs
            });
            Logger.Msg($"[BOAM] Command queued: {action} ({x},{z}) {skill}");
            return JsonSerializer.Serialize(new { status = "queued", action, x, z, skill });
        });
        _commandServer.Start();

        // Load modpack config (independent from engine)
        var modFolder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", "BOAM");
        Boundary.GameEvents.Init(modFolder, Logger);
        GameEvents.OnTurnEndEvent.InitEnrichments();
        GameEvents.TileScoresEvent.InitEnrichments();

        // Initialize tactical map overlay
        _tacticalMap = new TacticalMap.TacticalMapOverlay();
        _tacticalMap.Initialize(Logger, modFolder);

        Logger.Msg("[BOAM] Bridge plugin initialized, patches registered");
    }

    public void OnSceneLoaded(int buildIndex, string sceneName)
    {
        if (!_engineAvailable)
        {
            var thread = new Thread(CheckEngine)
            {
                Name = "BOAM-Check",
                IsBackground = true
            };
            thread.Start();
        }

        if (_engineAvailable)
            GameEvents.SceneChangeEvent.Process(sceneName);

        _tacticalMap?.OnSceneLoaded(sceneName);

        if (sceneName == "Tactical")
        {
            _inTactical = true;
            _initDelay = 60;
            _ready = false;
            // battle-start event fires later at tactical-ready, after preview data is copied
        }
        else
        {
            if (_inTactical)
            {
                GameEvents.BattleEndEvent.Process();
                TacticalMap.TacticalMapState.Reset();
            }
            _inTactical = false;
            _ready = false;
        }

    }


    public void OnUpdate()
    {
        _tacticalMap?.OnUpdate();

        // Tactical init delay
        if (_inTactical && !_ready)
        {
            if (_initDelay > 0) { _initDelay--; return; }
            _ready = true;
            Logger.Msg("[BOAM] Tactical ready, engine events active");
            // BuildDramatisPersonae must run first — it registers actor UUIDs
            var dp = ActorRegistry.BuildDramatisPersonae(Logger);
            GameEvents.MinimapUnitsEvent.PopulateInitial(Round);
            GameEvents.PreviewReadyEvent.ReloadFromDisk(Logger);
            _tacticalMap?.OnTacticalReady();
            GameEvents.BattleStartEvent.Process();
            if (_engineAvailable)
                GameEvents.TacticalReadyEvent.Process(dp);
        }

        // Drain execute command queue
        if (UnityEngine.Time.time >= _nextCommandTime && _executeQueue.TryDequeue(out var cmd))
        {
            BoamCommandExecutor.Execute(cmd, Logger);
            _nextCommandTime = UnityEngine.Time.time + (cmd.DelayMs / 1000f);
        }
    }


    private void CheckEngine()
    {
        const int maxRetries = 5;
        const int retryDelayMs = 2000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            if (_engineAvailable) return; // another thread already connected

            Logger.Msg($"[BOAM] Checking tactical engine (attempt {attempt}/{maxRetries})");

            var json = QueryCommandClient.Query("status");
            if (json != null)
            {
                Logger.Msg("[BOAM] Tactical engine connected");

                // Fetch feature flags from separate endpoint
                var featJson = QueryCommandClient.Query("features");
                if (featJson != null)
                {
                    try
                    {
                        var features = JsonSerializer.Deserialize<JsonElement>(featJson);
                        CriterionLogging = features.TryGetProperty("criterionLogging", out var cl) && cl.GetBoolean();
                        HeatmapsEnabled = features.TryGetProperty("heatmaps", out var hm) && hm.GetBoolean();
                        ActionLoggingEnabled = features.TryGetProperty("actionLogging", out var al) && al.GetBoolean();
                        AiLoggingEnabled = features.TryGetProperty("aiLogging", out var ai) && ai.GetBoolean();
                        Logger.Msg($"[BOAM] Features: criterion={CriterionLogging} heatmaps={HeatmapsEnabled} actions={ActionLoggingEnabled} ai={AiLoggingEnabled}");
                    }
                    catch { }
                }

                _engineAvailable = true;
                return;
            }

            if (attempt < maxRetries)
                Thread.Sleep(retryDelayMs);
        }

        Logger.Warning("[BOAM] Tactical engine not available — AI events will be no-ops");
    }



    public int Round => Menace.SDK.TacticalController.GetCurrentRound();
    public void ShowToast(string text, float seconds = 3f) => Toast.Show(text, seconds);



    public void OnGUI() { Toast.OnGUI(); _tacticalMap?.OnGUI(); }

    public void OnUnload()
    {
        _commandServer?.Stop();
    }
}
