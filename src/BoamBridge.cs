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
/// at game hook checkpoints and applies the returned modifications.
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

    /// <summary>Tactical scene ready AND engine is connected (for hooks that POST to the engine).</summary>
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
        Boundary.ModpackConfig.Load(modFolder, Logger);
        Boundary.GameEvents.Init(modFolder, Logger);

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

        // Notify tactical engine of every scene change
        if (_engineAvailable && !string.IsNullOrEmpty(sceneName) && GameEvents.SceneChangeEvent.IsActive)
        {
            var scenePayload = JsonSerializer.Serialize(new { scene = sceneName });
            ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("scene-change", scenePayload));
        }

        _tacticalMap?.OnSceneLoaded(sceneName);

        if (sceneName == "Tactical")
        {
            _inTactical = true;
            _initDelay = 60;
            _ready = false;
            // battle-start hook fires later at tactical-ready, after preview data is copied
        }
        else
        {
            if (_inTactical)
            {
                if (GameEvents.BattleEndEvent.IsActive)
                    ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("battle-end", "{}"));
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
            Logger.Msg("[BOAM] Tactical ready, engine hooks active");
            // BuildDramatisPersonae must run first — it registers actor UUIDs
            var dp = ActorRegistry.BuildDramatisPersonae(Logger);
            PopulateInitialUnits();
            ReloadMapFromDisk(); // copies preview → battle report, sets BattleSessionDir
            _tacticalMap?.OnTacticalReady();
            // battle-start after ReloadMapFromDisk so BattleSessionDir is set
            if (GameEvents.BattleStartEvent.IsActive)
            {
                var sessionDir = TacticalMap.TacticalMapState.BattleSessionDir ?? "";
                var startPayload = JsonSerializer.Serialize(new { sessionDir });
                ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("battle-start", startPayload));
            }
            if (_engineAvailable && GameEvents.TacticalReadyEvent.IsActive)
            {
                var payload = JsonSerializer.Serialize(new { dramatis_personae = dp });
                ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("tactical-ready", payload));
            }
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

        Logger.Warning("[BOAM] Tactical engine not available — AI hooks will be no-ops");
    }

    private void ReloadMapFromDisk()
    {
        try
        {
            var previewDir = TacticalMap.TacticalMapState.PreviewDir;
            if (string.IsNullOrEmpty(previewDir) || !System.IO.Directory.Exists(previewDir))
            {
                Logger.Warning("[BOAM] TacticalMap — No preview dir, cannot reload map");
                return;
            }

            var previewBg = System.IO.Path.Combine(previewDir, "mapbg.png");
            var previewInfo = System.IO.Path.Combine(previewDir, "mapbg.info");
            var previewData = System.IO.Path.Combine(previewDir, "mapdata.bin");

            if (!System.IO.File.Exists(previewBg))
            {
                Logger.Warning("[BOAM] TacticalMap — mapbg.png missing from preview dir");
                return;
            }

            // Create the real battle report dir (timestamped)
            var persistentDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData", "BOAM");

            var ts = DateTime.Now.ToString("yyyy_MM_dd_HH_mm");
            var sessionDir = System.IO.Path.Combine(persistentDir, "battle_reports", $"battle_{ts}");
            System.IO.Directory.CreateDirectory(sessionDir);
            TacticalMap.TacticalMapState.BattleSessionDir = sessionDir;

            // Copy preview files to battle report
            var bgPath = System.IO.Path.Combine(sessionDir, "mapbg.png");
            var infoPath = System.IO.Path.Combine(sessionDir, "mapbg.info");
            var dataPath = System.IO.Path.Combine(sessionDir, "mapdata.bin");

            System.IO.File.Copy(previewBg, bgPath, true);
            if (System.IO.File.Exists(previewInfo)) System.IO.File.Copy(previewInfo, infoPath, true);
            if (System.IO.File.Exists(previewData)) System.IO.File.Copy(previewData, dataPath, true);

            Logger.Msg($"[BOAM] TacticalMap — Copied preview data to {sessionDir}");

            // Load into state
            var data = TacticalMap.MapDataLoader.Load(bgPath, infoPath, dataPath, Logger);
            if (data.BackgroundTexture != null)
            {
                TacticalMap.TacticalMapState.MapTexture = data.BackgroundTexture;
                TacticalMap.TacticalMapState.TilesX = data.TotalX;
                TacticalMap.TacticalMapState.TilesZ = data.TotalZ;
                TacticalMap.TacticalMapState.TileDataArray = data.Tiles;
                TacticalMap.TacticalMapState.HeightMin = data.HeightMin;
                TacticalMap.TacticalMapState.HeightMax = data.HeightMax;
                Logger.Msg($"[BOAM] TacticalMap — Reloaded map from disk: {data.TotalX}x{data.TotalZ}");
            }
            else
            {
                Logger.Warning("[BOAM] TacticalMap — Failed to load map from copied files");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[BOAM] ReloadMapFromDisk error: {ex.Message}");
        }
    }

    private void PopulateInitialUnits()
    {
        try
        {
            var units = new System.Collections.Generic.List<TacticalMap.OverlayUnit>();
            var allActors = Menace.SDK.EntitySpawner.ListEntities(-1);
            if (allActors == null) return;
            foreach (var a in allActors)
            {
                var info = Menace.SDK.EntitySpawner.GetEntityInfo(a);
                if (info == null || !info.IsAlive) continue;
                var pos = Menace.SDK.EntityMovement.GetPosition(a);
                if (pos == null) continue;

                int vis = Menace.SDK.LineOfSight.GetVisibilityState(a);
                bool playerSide = info.FactionIndex == 1 || info.FactionIndex == 2 || info.FactionIndex == 4;
                bool known = playerSide || vis == 1 || vis == 3;

                // Get template name for icon lookup
                var go = new Menace.SDK.GameObj(a.Pointer);
                var tplObj = go.ReadObj("m_Template");
                var templateName = tplObj.IsNull ? "" : (tplObj.GetName() ?? "");

                var leaderName = "";
                try
                {
                    var unitActor = new Il2CppMenace.Tactical.UnitActor(a.Pointer);
                    var leader = unitActor.GetLeader();
                    if (leader != null)
                    {
                        var nn = leader.GetNickname();
                        if (nn != null) leaderName = nn.GetTranslated() ?? "";
                    }
                }
                catch { }

                var uuid = ActorRegistry.GetUuid(info.EntityId);
                units.Add(new TacticalMap.OverlayUnit
                {
                    Actor = uuid,
                    Label = uuid, // same as heatmap: stable UUID
                    FactionIndex = info.FactionIndex,
                    X = pos.Value.x,
                    Y = pos.Value.y,
                    KnownToPlayer = known,
                    Template = templateName,
                    Leader = leaderName
                });
            }
            TacticalMap.TacticalMapState.SetUnits(units);
            TacticalMap.TacticalMapState.CurrentRound = Round;
            Logger.Msg($"[BOAM] TacticalMap — Initial population: {units.Count} units");
        }
        catch (Exception ex)
        {
            Logger.Error($"[BOAM] PopulateInitialUnits error: {ex.Message}");
        }
    }

    public int Round => Menace.SDK.TacticalController.GetCurrentRound();
    public void ShowToast(string text, float seconds = 3f) => Toast.Show(text, seconds);



    public void OnGUI() { Toast.OnGUI(); _tacticalMap?.OnGUI(); }

    public void OnUnload()
    {
        _commandServer?.Stop();
    }
}
