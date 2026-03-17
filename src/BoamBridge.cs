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
    private bool _inTactical;
    private int _initDelay;
    private bool _ready;
    private BoamCommandServer _commandServer;
    private float _nextCommandTime;
    internal bool _replayActive;
    internal bool _replayCameraFollow = true;
    private TacticalMap.TacticalMapOverlay _tacticalMap;



    public void OnInitialize(MelonLogger.Instance logger, HarmonyLib.Harmony harmony)
    {
        Instance = this;
        Logger = logger;
        _harmony = harmony;

        // Apply patches immediately on init thread (canonical pattern)
        _harmony.PatchAll(typeof(BoamBridge).Assembly);

        // Manual patch: TacticalState.EndTurn — same lookup as TacticalController.EndTurn()
        try
        {
            var tsType = GameType.Find("Menace.States.TacticalState")?.ManagedType;
            if (tsType != null)
            {
                var endTurnMethod = tsType.GetMethod("EndTurn", BindingFlags.Public | BindingFlags.Instance);
                if (endTurnMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(Patch_EndTurn), nameof(Patch_EndTurn.Prefix));
                    _harmony.Patch(endTurnMethod, prefix: prefix);
                    Logger.Msg("[BOAM] Patched TacticalState.EndTurn for player endturn logging");
                }
                else
                    Logger.Warning("[BOAM] TacticalState.EndTurn method not found");
            }
            else
                Logger.Warning("[BOAM] TacticalState type not found — endturn logging disabled");
        }
        catch (Exception ex)
        {
            Logger.Error($"[BOAM] Failed to patch TacticalState.EndTurn: {ex.Message}");
        }

        // Manual patch: TacticalState.OnActiveActorChanged — fires when active actor changes
        try
        {
            var tsType2 = GameType.Find("Menace.States.TacticalState")?.ManagedType;
            if (tsType2 != null)
            {
                var onActorChanged = tsType2.GetMethod("OnActiveActorChanged", BindingFlags.Public | BindingFlags.Instance);
                if (onActorChanged != null)
                {
                    harmony.Patch(onActorChanged,
                        postfix: new HarmonyMethod(typeof(Patch_ActiveActorChanged), nameof(Patch_ActiveActorChanged.Postfix)));
                    Logger.Msg("[BOAM] Patched TacticalState.OnActiveActorChanged");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[BOAM] Failed to patch OnActiveActorChanged: {ex.Message}");
        }

        // Manual patch: MissionPrepUIScreen.OnPreviewReady — fires when mission preview map is loaded
        try
        {
            var prepType = typeof(Il2CppMenace.UI.Strategy.MissionPrepUIScreen);
            var onPreviewReady = prepType.GetMethod("OnPreviewReady");
            if (onPreviewReady != null)
            {
                harmony.Patch(onPreviewReady,
                    postfix: new HarmonyMethod(typeof(Patch_PreviewReady), nameof(Patch_PreviewReady.Postfix)));
                Logger.Msg("[BOAM] Patched MissionPrepUIScreen.OnPreviewReady");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[BOAM] Failed to patch OnPreviewReady: {ex.Message}");
        }

        // Primitive logging: patch HandleLeftClickOnTile on ALL known action classes
        // to discover which ones actually fire during gameplay
        try
        {
            var clickPostfix = new HarmonyMethod(typeof(Patch_ClickOnTile), nameof(Patch_ClickOnTile.Postfix));
            // Only patch types that override HandleLeftClickOnTile (verified from extracted scripts).
            // TravelPathAction, TravelAndEnterAction, OffmapSelectAoETilesAction do NOT override —
            // patching inherited virtuals causes Harmony warnings and potential native crashes.
            var actionTypes = new[] {
                typeof(Il2CppMenace.States.NoneAction),           // first click: path preview
                typeof(Il2CppMenace.States.ComputePathAction),    // second click: confirm move
                typeof(Il2CppMenace.States.SkillAction),          // skill targeting clicks
                typeof(Il2CppMenace.States.SelectAoETilesAction), // AoE tile selection
                typeof(Il2CppMenace.States.OffmapAbilityAction),  // offmap ability clicks
            };
            foreach (var actionType in actionTypes)
            {
                try
                {
                    var method = actionType.GetMethod("HandleLeftClickOnTile");
                    if (method != null)
                    {
                        harmony.Patch(method, postfix: clickPostfix);
                        Logger.Msg($"[BOAM] Patched {actionType.Name}.HandleLeftClickOnTile");
                    }
                    else
                    {
                        Logger.Msg($"[BOAM] {actionType.Name}: HandleLeftClickOnTile not found (inherits base)");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[BOAM] {actionType.Name} patch failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[BOAM] Failed to patch click logging: {ex.Message}");
        }

        // Primitive logging: patch TrySelectSkill on TacticalState
        try
        {
            var tsType3 = GameType.Find("Menace.States.TacticalState")?.ManagedType;
            if (tsType3 != null)
            {
                var trySelect = tsType3.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "TrySelectSkill" && m.GetParameters().Length == 1);
                if (trySelect != null)
                {
                    harmony.Patch(trySelect,
                        postfix: new HarmonyMethod(typeof(Patch_SelectSkill), nameof(Patch_SelectSkill.Postfix)));
                    Logger.Msg("[BOAM] Patched TacticalState.TrySelectSkill");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[BOAM] Failed to patch TrySelectSkill: {ex.Message}");
        }

        // Diagnostic: patch key TacticalManager events to trace turn/skill lifecycle
        try
        {
            var tmType = typeof(Il2CppMenace.Tactical.TacticalManager);
            var m = tmType.GetMethods().FirstOrDefault(m => m.Name == "InvokeOnTurnEnd" && m.GetParameters().Length == 1);
            if (m != null) { harmony.Patch(m, postfix: new HarmonyMethod(typeof(Patch_Diagnostics), nameof(Patch_Diagnostics.OnTurnEnd))); Logger.Msg("[BOAM] DIAG: Patched InvokeOnTurnEnd"); }

            m = tmType.GetMethods().FirstOrDefault(m => m.Name == "InvokeOnAfterSkillUse" && m.GetParameters().Length == 1);
            if (m != null) { harmony.Patch(m, postfix: new HarmonyMethod(typeof(Patch_Diagnostics), nameof(Patch_Diagnostics.OnAfterSkillUse))); Logger.Msg("[BOAM] DIAG: Patched InvokeOnAfterSkillUse"); }

            m = tmType.GetMethods().FirstOrDefault(m => m.Name == "InvokeOnAttackTileStart" && m.GetParameters().Length == 4);
            if (m != null) { harmony.Patch(m, postfix: new HarmonyMethod(typeof(Patch_Diagnostics), nameof(Patch_Diagnostics.OnAttackTileStart))); Logger.Msg("[BOAM] DIAG: Patched InvokeOnAttackTileStart"); }

            m = tmType.GetMethods().FirstOrDefault(m => m.Name == "InvokeOnActionPointsChanged" && m.GetParameters().Length == 3);
            if (m != null) { harmony.Patch(m, postfix: new HarmonyMethod(typeof(Patch_Diagnostics), nameof(Patch_Diagnostics.OnActionPointsChanged))); Logger.Msg("[BOAM] DIAG: Patched InvokeOnActionPointsChanged"); }
        }
        catch (Exception ex)
        {
            Logger.Error($"[BOAM] DIAG patches failed: {ex.Message}");
        }

        // AI action logging: patch TacticalManager events to capture actual AI actions
        try
        {
            var tmType = typeof(Il2CppMenace.Tactical.TacticalManager);

            var m = tmType.GetMethods().FirstOrDefault(m => m.Name == "InvokeOnMovementFinished" && m.GetParameters().Length == 2);
            if (m != null) { harmony.Patch(m, postfix: new HarmonyMethod(typeof(AiActionPatches), nameof(AiActionPatches.OnMovementFinished))); Logger.Msg("[BOAM] Patched InvokeOnMovementFinished for AI action logging"); }

            m = tmType.GetMethods().FirstOrDefault(m => m.Name == "InvokeOnSkillUse" && m.GetParameters().Length == 3);
            if (m != null) { harmony.Patch(m, postfix: new HarmonyMethod(typeof(AiActionPatches), nameof(AiActionPatches.OnSkillUse))); Logger.Msg("[BOAM] Patched InvokeOnSkillUse for AI action logging"); }

            m = tmType.GetMethods().FirstOrDefault(m => m.Name == "InvokeOnTurnEnd" && m.GetParameters().Length == 1);
            if (m != null) { harmony.Patch(m, postfix: new HarmonyMethod(typeof(AiActionPatches), nameof(AiActionPatches.OnTurnEnd))); Logger.Msg("[BOAM] Patched InvokeOnTurnEnd for AI action logging"); }
        }
        catch (Exception ex)
        {
            Logger.Error($"[BOAM] AI action patches failed: {ex.Message}");
        }

        // Per-element hit logging: patch Element.OnHit for atomic combat logging
        try
        {
            var elementType = typeof(Il2CppMenace.Tactical.Element);
            // Dump hit/damage methods for diagnostics
            foreach (var method in elementType.GetMethods())
            {
                var name = method.Name;
                if (name.Contains("Hit") || name.Contains("Damage") || name.Contains("Death"))
                {
                    var parms = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    Logger.Msg($"[BOAM] ELEMENT METHOD: {name}({parms}) params={method.GetParameters().Length}");
                }
            }
            var m = elementType.GetMethods().FirstOrDefault(m => m.Name == "OnHit");
            if (m != null)
            {
                var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                Logger.Msg($"[BOAM] Found Element.OnHit: {parms} params={m.GetParameters().Length}");
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(AiActionPatches), nameof(AiActionPatches.OnElementHit)));
                Logger.Msg("[BOAM] Patched Element.OnHit for combat logging");
            }
            else Logger.Warning("[BOAM] Element.OnHit not found");
        }
        catch (Exception ex)
        {
            Logger.Error($"[BOAM] Element hit patch failed: {ex.Message}");
        }

        // Start command server for receiving actions from tactical engine
        _commandServer = new BoamCommandServer(Logger);
        _commandServer.Start();

        // Initialize tactical map overlay
        _tacticalMap = new TacticalMap.TacticalMapOverlay();
        var modFolder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", "BOAM");
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
        if (_engineAvailable && !string.IsNullOrEmpty(sceneName))
        {
            var scenePayload = JsonSerializer.Serialize(new { scene = sceneName });
            ThreadPool.QueueUserWorkItem(_ => EngineClient.Post("/hook/scene-change", scenePayload));
        }

        _tacticalMap?.OnSceneLoaded(sceneName);

        if (sceneName == "Tactical")
        {
            _inTactical = true;
            _initDelay = 60;
            _ready = false;
            // Tell the tactical engine about the battle session (dir already created at OnPreviewReady)
            var sessionDir = TacticalMap.TacticalMapState.BattleSessionDir ?? "";
            var startPayload = JsonSerializer.Serialize(new { sessionDir });
            ThreadPool.QueueUserWorkItem(_ => EngineClient.Post("/hook/battle-start", startPayload));
        }
        else
        {
            if (_inTactical)
            {
                // End battle session when leaving tactical
                ThreadPool.QueueUserWorkItem(_ => EngineClient.Post("/hook/battle-end", "{}"));
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
            ReloadMapFromDisk();
            _tacticalMap?.OnTacticalReady();
            if (_engineAvailable)
            {
                var payload = JsonSerializer.Serialize(new { dramatis_personae = dp });
                ThreadPool.QueueUserWorkItem(_ => EngineClient.Post("/hook/tactical-ready", payload));
            }
        }

        // Pull-based replay: bridge asks the engine for the next action when ready.
        // Gates: right actor active, not moving, no skill animation in progress.
        if (_engineAvailable && _ready && _replayActive && UnityEngine.Time.time >= _nextCommandTime)
        {
            // Check gates before pulling
            var activeGameObj = TacticalController.GetActiveActor();
            if (activeGameObj.IsNull) return;
            var activeActor = new Actor(activeGameObj.Pointer);
            if (activeActor.IsMoving()) return;

            var activeInfo = ActorRegistry.GetActorInfo(activeActor);
            if (activeInfo == null) return;
            var activeUuid = ActorRegistry.GetUuid(activeInfo.Value.entityId);
            var activeFaction = activeInfo.Value.factionId;

            // Only pull during player turns — check both active actor AND current game faction
            if (activeFaction != 1 && activeFaction != 2) return;
            int gameFaction = TacticalController.GetCurrentFaction();
            if (gameFaction != 1 && gameFaction != 2) return;

            try
            {
                var json = EngineClient.Get($"/replay/next?actor={Uri.EscapeDataString(activeUuid)}&round={Round}");
                if (json == null) return;
                var doc = JsonSerializer.Deserialize<JsonElement>(json);

                var status = doc.GetProperty("status").GetString();
                var respActor = doc.TryGetProperty("actor", out var ra) ? ra.GetString() ?? "" : "";
                var respAction = doc.TryGetProperty("action", out var ract) ? ract.GetString() ?? "" : "";
                Logger.Msg($"[BOAM] Replay pull: active={activeUuid} faction={activeFaction} gameFaction={gameFaction} → status={status} actor={respActor} action={respAction}");
                if (status == "done")
                {
                    _replayActive = false;
                    Logger.Msg("[BOAM] Replay complete");
                    Toast.Show("Replay complete", 5f);
                    return;
                }
                if (status == "halted")
                {
                    _replayActive = false;
                    var reason = doc.TryGetProperty("reason", out var rv) ? rv.GetString() ?? "" : "";
                    var divCount = doc.TryGetProperty("divergences", out var dcv) ? dcv.GetInt32() : 0;
                    Logger.Msg($"[BOAM] Replay HALTED: {reason}, {divCount} divergence(s)");
                    Toast.Show($"Replay halted: {reason} ({divCount} divergence(s))", 10f);
                    return;
                }
                if (status == "waiting")
                    return;

                var action = doc.GetProperty("action").GetString() ?? "";
                var x = doc.TryGetProperty("x", out var xv) ? xv.GetInt32() : 0;
                var z = doc.TryGetProperty("z", out var zv) ? zv.GetInt32() : 0;
                var skill = doc.TryGetProperty("skill", out var sv) ? sv.GetString() ?? "" : "";
                var delayMs = doc.TryGetProperty("delay_ms", out var dv) ? dv.GetInt32() : 0;

                // Use the actor from the response, not the active actor — the engine may
                // serve select actions for a different actor (player switching units).
                var cmdActor = !string.IsNullOrEmpty(respActor) ? respActor : activeUuid;
                var cmd = new BoamCommandServer.ActionCommand
                {
                    Action = action, X = x, Z = z, Skill = skill, Actor = cmdActor, DelayMs = delayMs
                };

                Logger.Msg($"[BOAM] Replay exec: {cmdActor} {action} ({x},{z}) {skill} delayMs={delayMs}");
                BoamCommandExecutor.Execute(cmd, Logger);
                _nextCommandTime = UnityEngine.Time.time + (delayMs / 1000f);
            }
            catch (Exception ex)
            {
                Logger.Warning($"[BOAM] Replay pull error: {ex.Message}");
            }
        }

        // Also drain command server queue for non-replay commands
        if (_commandServer != null && UnityEngine.Time.time >= _nextCommandTime)
        {
            if (_commandServer.TryDequeue(out var cmd))
            {
                BoamCommandExecutor.Execute(cmd, Logger);
                _nextCommandTime = UnityEngine.Time.time + (cmd.DelayMs / 1000f);
            }
        }
    }


    private void CheckEngine()
    {
        const int maxRetries = 5;
        const int retryDelayMs = 2000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            Logger.Msg($"[BOAM] Checking tactical engine (attempt {attempt}/{maxRetries})");

            var json = EngineClient.Get("/status");
            if (json != null)
            {
                try
                {
                    var doc = JsonSerializer.Deserialize<JsonElement>(json);
                    var status = doc.GetProperty("status").GetString();
                    Logger.Msg($"[BOAM] Tactical engine found (status: {status})");
                    _engineAvailable = true;

                    return;
                }
                catch { }
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
            var sessionDir = TacticalMap.TacticalMapState.BattleSessionDir;
            if (string.IsNullOrEmpty(sessionDir))
            {
                Logger.Warning("[BOAM] TacticalMap — No battle session dir, cannot reload map");
                return;
            }
            var bgPath = System.IO.Path.Combine(sessionDir, "mapbg.png");
            var infoPath = System.IO.Path.Combine(sessionDir, "mapbg.info");
            var dataPath = System.IO.Path.Combine(sessionDir, "mapdata.bin");

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
                Logger.Warning("[BOAM] TacticalMap — No map background on disk to reload");
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

    public bool IsReady => _ready && _engineAvailable;
    public int Round => Menace.SDK.TacticalController.GetCurrentRound();
    public void ShowToast(string text, float seconds = 3f) => Toast.Show(text, seconds);



    public void OnGUI() { Toast.OnGUI(); _tacticalMap?.OnGUI(); }

    public void OnUnload()
    {
        _commandServer?.Stop();
        if (_engineAvailable)
        {
            try { EngineClient.Post("/shutdown", "{}"); } catch { }
            Logger.Msg("[BOAM] Sent shutdown to tactical engine");
        }
    }
}
