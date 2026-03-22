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
    private BoamCommandServer _commandServer;
    private float _nextCommandTime;
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

        // Manual patch: MissionPrepUIScreen.LaunchMission — captures map data before scene transition
        try
        {
            var prepType2 = typeof(Il2CppMenace.UI.Strategy.MissionPrepUIScreen);
            var launchMission = prepType2.GetMethod("LaunchMission", BindingFlags.NonPublic | BindingFlags.Instance);
            if (launchMission != null)
            {
                harmony.Patch(launchMission,
                    prefix: new HarmonyMethod(typeof(Patch_LaunchMission), nameof(Patch_LaunchMission.Prefix)));
                Logger.Msg("[BOAM] Patched MissionPrepUIScreen.LaunchMission");
            }
            else
                Logger.Warning("[BOAM] MissionPrepUIScreen.LaunchMission not found");
        }
        catch (Exception ex)
        {
            Logger.Error($"[BOAM] Failed to patch LaunchMission: {ex.Message}");
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

        // Per-element hit logging: patch Element.OnHit postfix
        try
        {
            var elementType = typeof(Il2CppMenace.Tactical.Element);
            var m = elementType.GetMethods().FirstOrDefault(m => m.Name == "OnHit");
            if (m != null)
            {
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(AiActionPatches), nameof(AiActionPatches.OnElementHit)));
                Logger.Msg("[BOAM] Patched Element.OnHit for logging");
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

        // Load modpack config (independent from engine)
        var modFolder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Mods", "BOAM");
        Boundary.ModpackConfig.Load(modFolder, Logger);

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
            SetupTestTileModifiers();
            ReloadMapFromDisk();
            _tacticalMap?.OnTacticalReady();
            if (_engineAvailable)
            {
                var payload = JsonSerializer.Serialize(new { dramatis_personae = dp });
                ThreadPool.QueueUserWorkItem(_ => EngineClient.Post("/hook/tactical-ready", payload));
            }
        }

        // Update test shape based on current round
        if (_inTactical && _ready)
            UpdateTestTileModifiers();

        // Drain command server queue
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

                    // Parse feature flags from engine
                    if (doc.TryGetProperty("features", out var features))
                    {
                        CriterionLogging = features.TryGetProperty("criterionLogging", out var cl) && cl.GetBoolean();
                        HeatmapsEnabled = features.TryGetProperty("heatmaps", out var hm) && hm.GetBoolean();
                        ActionLoggingEnabled = features.TryGetProperty("actionLogging", out var al) && al.GetBoolean();
                        AiLoggingEnabled = features.TryGetProperty("aiLogging", out var ai) && ai.GetBoolean();
                        Logger.Msg($"[BOAM] Features: criterion={CriterionLogging} heatmaps={HeatmapsEnabled} actions={ActionLoggingEnabled} ai={AiLoggingEnabled}");
                    }

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

    // BOAM shape test data (see docs/next/tile-modifier-test-shape.txt)
    private static readonly (int x, int z)[][] TestShapes = {
        new (int,int)[] { // B
            (2,15),(2,17),(2,19),(2,21),(2,23),(2,25),(2,27),
            (4,27),(6,27),(8,27),(10,27),(4,21),(6,21),(8,21),(10,21),
            (4,15),(6,15),(8,15),(10,15),(10,25),(10,23),(10,19),(10,17),
            (8,25),(8,23),(8,19),(8,17),
        },
        new (int,int)[] { // O
            (12,15),(12,17),(12,19),(12,21),(12,23),(12,25),(12,27),
            (20,15),(20,17),(20,19),(20,21),(20,23),(20,25),(20,27),
            (14,27),(16,27),(18,27),(14,15),(16,15),(18,15),
            (14,25),(18,25),(14,17),(18,17),(16,23),(16,21),(16,19),
        },
        new (int,int)[] { // A
            (22,15),(22,17),(22,19),(22,21),(22,23),(22,25),(22,27),
            (30,15),(30,17),(30,19),(30,21),(30,23),(30,25),(30,27),
            (24,27),(26,27),(28,27),(24,21),(26,21),(28,21),
            (24,25),(28,25),(24,23),(28,23),(24,19),(26,19),(28,19),
        },
        new (int,int)[] { // M
            (32,15),(32,17),(32,19),(32,21),(32,23),(32,25),(32,27),
            (40,15),(40,17),(40,19),(40,21),(40,23),(40,25),(40,27),
            (34,27),(36,27),(38,27),(34,25),(36,23),(38,25),(36,21),
            (34,23),(38,23),(34,15),(36,15),(38,15),(36,19),
        },
    };
    private static readonly string[] ShapeNames = { "B", "O", "A", "M" };
    private string[] _testAiActors;
    private int _lastShapeIndex = -1;

    private void SetupTestTileModifiers()
    {
        var aiActors = new System.Collections.Generic.List<string>();
        var allActors = Menace.SDK.EntitySpawner.ListEntities(-1);
        if (allActors == null) return;
        foreach (var a in allActors)
        {
            var info = Menace.SDK.EntitySpawner.GetEntityInfo(a);
            if (info == null || !info.IsAlive || info.FactionIndex == 1) continue;
            aiActors.Add(ActorRegistry.GetUuid(info.EntityId));
        }
        _testAiActors = aiActors.ToArray();
        _lastShapeIndex = -1;
        ApplyTestShape(0);
    }

    private void UpdateTestTileModifiers()
    {
        if (_testAiActors == null) return;
        int round = Round;
        int shapeIndex;
        if (round <= 15)
            shapeIndex = 0;
        else
            shapeIndex = 1 + (round - 16) / 10;
        if (shapeIndex >= TestShapes.Length) { TileModifierStore.Clear(); return; }
        if (shapeIndex != _lastShapeIndex)
            ApplyTestShape(shapeIndex);
    }

    private void ApplyTestShape(int shapeIndex)
    {
        if (_testAiActors == null || shapeIndex >= TestShapes.Length) return;
        TileModifierStore.Clear();
        var targets = TestShapes[shapeIndex];
        int count = System.Math.Min(_testAiActors.Length, targets.Length);
        for (int i = 0; i < count; i++)
        {
            var (tx, tz) = targets[i];
            TileModifierStore.Set(_testAiActors[i], new TileModifierStore.TileModifier
            {
                AddUtility = 20000f, MultCombined = 1f,
                MinDistance = 0f, MaxDistance = 0f,
                TargetX = tx, TargetZ = tz,
                SuppressAttack = true
            });
        }
        _lastShapeIndex = shapeIndex;
        Logger.Msg($"[BOAM] TileModifier TEST: shape {ShapeNames[shapeIndex]} applied to {count} actors (rounds {shapeIndex*20+1}-{(shapeIndex+1)*20})");
    }

    public int Round => Menace.SDK.TacticalController.GetCurrentRound();
    public void ShowToast(string text, float seconds = 3f) => Toast.Show(text, seconds);



    public void OnGUI() { Toast.OnGUI(); _tacticalMap?.OnGUI(); }

    public void OnUnload()
    {
        _commandServer?.Stop();
    }
}
