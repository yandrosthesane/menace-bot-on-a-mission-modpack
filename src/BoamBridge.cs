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
    public static MelonLogger.Instance Log { get; private set; }

    private HarmonyLib.Harmony _harmony;
    private volatile bool _engineAvailable;
    private bool _inTactical;
    private int _initDelay;
    private bool _ready;
    private BoamCommandServer _commandServer;
    private float _nextCommandTime;
    internal static float SkillAnimationEndTime;
    internal bool _replayActive;

    // Stable actor UUID registry — built once at tactical-ready, used by all hooks
    private static System.Collections.Generic.Dictionary<int, string> _entityToUuid = new();
    private static System.Collections.Generic.Dictionary<string, int> _uuidToEntity = new();

    /// Resolve entity ID to stable UUID. Returns "unknown.{entityId}" if not found.
    public static string GetUuid(int entityId)
    {
        return _entityToUuid.TryGetValue(entityId, out var uuid) ? uuid : $"unknown.{entityId}";
    }

    /// Resolve stable UUID to entity ID. Returns -1 if not found.
    public static int GetEntityId(string uuid)
    {
        return _uuidToEntity.TryGetValue(uuid, out var id) ? id : -1;
    }

    /// Faction index to stable name (matches ActorRegistry.fs convention).
    private static string FactionName(int faction)
    {
        return faction switch
        {
            0 => "neutral",
            1 => "player",
            2 => "allied",
            3 => "civilian",
            4 => "allied_local",
            5 => "enemy_local",
            6 => "pirates",
            7 => "wildlife",
            8 => "constructs",
            9 => "rogue_army",
            _ => $"faction{faction}"
        };
    }

    /// Template short name — last segment after the dot.
    /// "player_squad.carda" → "carda", "enemy.alien_stinger" → "alien_stinger"
    private static string TemplateShort(string template)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var lastDot = template.LastIndexOf('.');
        return lastDot >= 0 ? template.Substring(lastDot + 1) : template;
    }

    // Synchronous HTTP — avoids async deadlocks under Wine CLR
    internal static string EngineGet(string path)
    {
        try
        {
            using var client = new System.Net.WebClient();
            return client.DownloadString("http://127.0.0.1:7660" + path);
        }
        catch { return null; }
    }

    internal static string EnginePost(string path, string json)
    {
        try
        {
            using var client = new System.Net.WebClient();
            client.Headers[System.Net.HttpRequestHeader.ContentType] = "application/json";
            return client.UploadString("http://127.0.0.1:7660" + path, json);
        }
        catch { return null; }
    }

    public void OnInitialize(MelonLogger.Instance logger, HarmonyLib.Harmony harmony)
    {
        Instance = this;
        Log = logger;
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
                    Log.Msg("[BOAM] Patched TacticalState.EndTurn for player endturn logging");
                }
                else
                    Log.Warning("[BOAM] TacticalState.EndTurn method not found");
            }
            else
                Log.Warning("[BOAM] TacticalState type not found — endturn logging disabled");
        }
        catch (Exception ex)
        {
            Log.Error($"[BOAM] Failed to patch TacticalState.EndTurn: {ex.Message}");
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
                    Log.Msg("[BOAM] Patched TacticalState.OnActiveActorChanged");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[BOAM] Failed to patch OnActiveActorChanged: {ex.Message}");
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
                Log.Msg("[BOAM] Patched MissionPrepUIScreen.OnPreviewReady");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[BOAM] Failed to patch OnPreviewReady: {ex.Message}");
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
                        Log.Msg($"[BOAM] Patched {actionType.Name}.HandleLeftClickOnTile");
                    }
                    else
                    {
                        Log.Msg($"[BOAM] {actionType.Name}: HandleLeftClickOnTile not found (inherits base)");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[BOAM] {actionType.Name} patch failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[BOAM] Failed to patch click logging: {ex.Message}");
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
                    Log.Msg("[BOAM] Patched TacticalState.TrySelectSkill");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[BOAM] Failed to patch TrySelectSkill: {ex.Message}");
        }

        // Diagnostic: patch key TacticalManager events to trace turn/skill lifecycle
        try
        {
            var tmType = typeof(Il2CppMenace.Tactical.TacticalManager);
            var m = tmType.GetMethods().FirstOrDefault(m => m.Name == "InvokeOnTurnEnd" && m.GetParameters().Length == 1);
            if (m != null) { harmony.Patch(m, postfix: new HarmonyMethod(typeof(Patch_Diagnostics), nameof(Patch_Diagnostics.OnTurnEnd))); Log.Msg("[BOAM] DIAG: Patched InvokeOnTurnEnd"); }

            m = tmType.GetMethods().FirstOrDefault(m => m.Name == "InvokeOnAfterSkillUse" && m.GetParameters().Length == 1);
            if (m != null) { harmony.Patch(m, postfix: new HarmonyMethod(typeof(Patch_Diagnostics), nameof(Patch_Diagnostics.OnAfterSkillUse))); Log.Msg("[BOAM] DIAG: Patched InvokeOnAfterSkillUse"); }

            m = tmType.GetMethods().FirstOrDefault(m => m.Name == "InvokeOnAttackTileStart" && m.GetParameters().Length == 4);
            if (m != null) { harmony.Patch(m, postfix: new HarmonyMethod(typeof(Patch_Diagnostics), nameof(Patch_Diagnostics.OnAttackTileStart))); Log.Msg("[BOAM] DIAG: Patched InvokeOnAttackTileStart"); }

            m = tmType.GetMethods().FirstOrDefault(m => m.Name == "InvokeOnActionPointsChanged" && m.GetParameters().Length == 3);
            if (m != null) { harmony.Patch(m, postfix: new HarmonyMethod(typeof(Patch_Diagnostics), nameof(Patch_Diagnostics.OnActionPointsChanged))); Log.Msg("[BOAM] DIAG: Patched InvokeOnActionPointsChanged"); }
        }
        catch (Exception ex)
        {
            Log.Error($"[BOAM] DIAG patches failed: {ex.Message}");
        }

        // Start command server for receiving actions from tactical engine
        _commandServer = new BoamCommandServer(Log);
        _commandServer.Start();

        Log.Msg("[BOAM] Bridge plugin initialized, patches registered");
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
            ThreadPool.QueueUserWorkItem(_ => EnginePost("/hook/scene-change", scenePayload));
        }

        if (sceneName == "Tactical")
        {
            _inTactical = true;
            _initDelay = 60;
            _ready = false;
            // Start a new battle session in the tactical engine
            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var startPayload = JsonSerializer.Serialize(new { timestamp = ts });
            ThreadPool.QueueUserWorkItem(_ => EnginePost("/hook/battle-start", startPayload));
        }
        else
        {
            if (_inTactical)
            {
                // End battle session when leaving tactical
                ThreadPool.QueueUserWorkItem(_ => EnginePost("/hook/battle-end", "{}"));
            }
            _inTactical = false;
            _ready = false;
        }

    }


    public void OnUpdate()
    {
        // Tactical init delay
        if (_inTactical && !_ready)
        {
            if (_initDelay > 0) { _initDelay--; return; }
            _ready = true;
            Log.Msg("[BOAM] Tactical ready, engine hooks active");
            if (_engineAvailable)
            {
                var dp = BuildDramatisPersonae();
                var payload = JsonSerializer.Serialize(new { dramatis_personae = dp });
                ThreadPool.QueueUserWorkItem(_ => EnginePost("/hook/tactical-ready", payload));
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
            if (UnityEngine.Time.time < SkillAnimationEndTime) return;

            var activeInfo = GetActorInfo(activeActor);
            if (activeInfo == null) return;
            var activeUuid = GetUuid(activeInfo.Value.entityId);
            var activeFaction = activeInfo.Value.factionId;

            // Only pull during player turns
            if (activeFaction != 1 && activeFaction != 2) return;

            try
            {
                var json = EngineGet($"/replay/next?actor={Uri.EscapeDataString(activeUuid)}&round={Round}");
                if (json == null) return;
                var doc = JsonSerializer.Deserialize<JsonElement>(json);

                var status = doc.GetProperty("status").GetString();
                if (status == "done" || status == "waiting")
                    return;

                var action = doc.GetProperty("action").GetString() ?? "";
                var x = doc.TryGetProperty("x", out var xv) ? xv.GetInt32() : 0;
                var z = doc.TryGetProperty("z", out var zv) ? zv.GetInt32() : 0;
                var skill = doc.TryGetProperty("skill", out var sv) ? sv.GetString() ?? "" : "";
                var delayMs = doc.TryGetProperty("delay_ms", out var dv) ? dv.GetInt32() : 0;

                var cmd = new BoamCommandServer.ActionCommand
                {
                    Action = action, X = x, Z = z, Skill = skill, Actor = activeUuid, DelayMs = delayMs
                };

                Log.Msg($"[BOAM] Replay exec: {activeUuid} {action} ({x},{z}) {skill}");
                BoamCommandExecutor.Execute(cmd, Log);
                _nextCommandTime = UnityEngine.Time.time + (delayMs / 1000f);
            }
            catch (Exception ex)
            {
                Log.Warning($"[BOAM] Replay pull error: {ex.Message}");
            }
        }

        // Also drain command server queue for non-replay commands
        if (_commandServer != null && UnityEngine.Time.time >= _nextCommandTime)
        {
            if (_commandServer.TryDequeue(out var cmd))
            {
                BoamCommandExecutor.Execute(cmd, Log);
                _nextCommandTime = UnityEngine.Time.time + (cmd.DelayMs / 1000f);
            }
        }
    }

    /// Build the full dramatis personae on the main thread.
    /// Computes stable UUIDs and populates the entity↔UUID lookup tables.
    private static System.Collections.Generic.List<object> BuildDramatisPersonae()
    {
        var result = new System.Collections.Generic.List<object>();
        var entries = new System.Collections.Generic.List<(int entityId, string template, int faction, string leader, int x, int z, bool isAlive)>();

        try
        {
            var allActors = EntitySpawner.ListEntities(-1);
            if (allActors == null) return result;
            foreach (var actor in allActors)
            {
                var info = EntitySpawner.GetEntityInfo(actor);
                if (info == null) continue;
                var pos = EntityMovement.GetPosition(actor);
                var go = new GameObj(actor.Pointer);
                var tplObj = go.ReadObj("m_Template");
                var templateName = tplObj.IsNull ? "" : (tplObj.GetName() ?? "");

                var leaderName = "";
                try
                {
                    var unitActor = new Il2CppMenace.Tactical.UnitActor(actor.Pointer);
                    var leader = unitActor.GetLeader();
                    if (leader != null)
                    {
                        var nickname = leader.GetNickname();
                        if (nickname != null)
                            leaderName = nickname.GetTranslated() ?? "";
                    }
                }
                catch { }

                entries.Add((info.EntityId, templateName, info.FactionIndex,
                    leaderName.ToLowerInvariant(), pos?.x ?? 0, pos?.y ?? 0, info.IsAlive));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[BOAM] BuildDramatisPersonae error: {ex.Message}");
            return result;
        }

        // Compute stable UUIDs: group by (faction, template), sort by position, assign occurrence
        var newEntityToUuid = new System.Collections.Generic.Dictionary<int, string>();
        var newUuidToEntity = new System.Collections.Generic.Dictionary<string, int>();

        var groups = entries
            .GroupBy(e => (e.faction, e.template))
            .ToList();

        foreach (var group in groups)
        {
            var sorted = group.OrderBy(e => e.x).ThenBy(e => e.z).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                var e = sorted[i];
                var factionStr = FactionName(e.faction);
                var shortTemplate = TemplateShort(e.template);

                string uuid;
                if (!string.IsNullOrEmpty(e.leader))
                    uuid = $"{factionStr}.{e.leader}";
                else
                    uuid = $"{factionStr}.{shortTemplate}.{i + 1}";

                newEntityToUuid[e.entityId] = uuid;
                newUuidToEntity[uuid] = e.entityId;

                result.Add(new
                {
                    actor = uuid,
                    template = e.template,
                    faction = e.faction,
                    leader = e.leader,
                    x = e.x,
                    z = e.z,
                    isAlive = e.isAlive
                });
            }
        }

        // Atomically swap the registries
        _entityToUuid = newEntityToUuid;
        _uuidToEntity = newUuidToEntity;
        Log.Msg($"[BOAM] Dramatis personae: {newEntityToUuid.Count} actors registered");

        return result;
    }

    private void CheckEngine()
    {
        const int maxRetries = 5;
        const int retryDelayMs = 2000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            Log.Msg($"[BOAM] Checking tactical engine (attempt {attempt}/{maxRetries})");

            var json = EngineGet("/status");
            if (json != null)
            {
                try
                {
                    var doc = JsonSerializer.Deserialize<JsonElement>(json);
                    var status = doc.GetProperty("status").GetString();
                    Log.Msg($"[BOAM] Tactical engine found (status: {status})");
                    _engineAvailable = true;

                    return;
                }
                catch { }
            }

            if (attempt < maxRetries)
                Thread.Sleep(retryDelayMs);
        }

        Log.Warning("[BOAM] Tactical engine not available — AI hooks will be no-ops");
    }

    public bool IsReady => _ready && _engineAvailable;
    public int Round => Menace.SDK.TacticalController.GetCurrentRound();

    /// Extract common actor info: GameObj, factionId, entityId, templateName.
    internal static (GameObj gameObj, int factionId, int entityId, string templateName)?
        GetActorInfo(Actor actor)
    {
        if (actor == null) return null;
        var gameObj = new GameObj(actor.Pointer);
        var info = EntitySpawner.GetEntityInfo(gameObj);
        var tplObj = gameObj.ReadObj("m_Template");
        var templateName = tplObj.IsNull ? "" : (tplObj.GetName() ?? "");
        return (gameObj, info?.FactionIndex ?? 0, info?.EntityId ?? 0, templateName);
    }

    /// Extract position from a GameObj via EntityMovement.
    internal static (int x, int z) GetPos(GameObj gameObj)
    {
        var pos = EntityMovement.GetPosition(gameObj);
        return pos.HasValue ? (pos.Value.x, pos.Value.y) : (0, 0);
    }


    public void OnGUI() { }

    public void OnUnload()
    {
        _commandServer?.Stop();
        if (_engineAvailable)
        {
            try { EnginePost("/shutdown", "{}"); } catch { }
            Log.Msg("[BOAM] Sent shutdown to tactical engine");
        }
    }
}

/// <summary>
/// Harmony patch: intercepts AIFaction.OnTurnStart and sends faction state to tactical engine.
/// Uses synchronous WebClient to avoid async deadlocks under Wine CLR.
/// </summary>
[HarmonyPatch(typeof(AIFaction), nameof(AIFaction.OnTurnStart))]
static class Patch_OnTurnStart
{
    static void Prefix(AIFaction __instance)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsReady) return;

            int factionIdx = __instance.GetIndex();

            var opponents = __instance.m_Opponents;
            int opponentCount = opponents?.Count ?? 0;

            var oppList = new System.Collections.Generic.List<object>();
            if (opponents != null)
            {
                for (int i = 0; i < opponents.Count; i++)
                {
                    try
                    {
                        var opp = opponents[i];
                        var actorInfo = BoamBridge.GetActorInfo(opp.Actor);
                        if (actorInfo == null) continue;
                        var (gameObj, _, entityId, _) = actorInfo.Value;
                        var (px, pz) = BoamBridge.GetPos(gameObj);

                        oppList.Add(new
                        {
                            actor = BoamBridge.GetUuid(entityId),
                            position = new { x = px, z = pz },
                            ttl = opp.TTL,
                            isKnown = opp.IsKnown(),
                            isAlive = opp.Actor.IsAlive()
                        });
                    }
                    catch { }
                }
            }

            var payload = JsonSerializer.Serialize(new
            {
                hook = "on-turn-start",
                faction = factionIdx,
                opponentCount,
                opponents = oppList
            });

            var response = BoamBridge.EnginePost("/hook/on-turn-start", payload);
            if (response != null)
            {
                BoamBridge.Log.Msg($"[BOAM] on-turn-start f{factionIdx}: engine OK ({response.Length}b, {oppList.Count} opponents)");
            }
        }
        catch (Exception ex)
        {
            BoamBridge.Log.Error($"[BOAM] on-turn-start error: {ex.Message}");
        }
    }
}

/// <summary>
/// Harmony patch: intercepts Agent.PostProcessTileScores to capture fully-scored tiles
/// after ALL criterions have evaluated and role-based weighting is applied.
/// This gives the complete picture: Utility (zones, effects), Safety (cover, threats),
/// Distance (movement cost), and the weighted Combined score used for behavior evaluation.
/// Fires once per agent, parallel across agents (safe — each agent has its own tiles).
/// </summary>
[HarmonyPatch(typeof(Agent), "PostProcessTileScores")]
static class Patch_PostProcessTileScores
{
    static void Postfix(Agent __instance)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsReady) return;

            var actor = __instance.m_Actor;
            if (actor == null) return;

            var tiles = __instance.m_Tiles;
            if (tiles == null || tiles.Count == 0) return;

            var info = BoamBridge.GetActorInfo(actor);
            if (info == null) return;
            var (gameObj, factionId, actorEntityId, _) = info.Value;
            var actorUuid = BoamBridge.GetUuid(actorEntityId);

            var (actorX, actorZ) = BoamBridge.GetPos(gameObj);

            var tileList = new System.Collections.Generic.List<object>();

            var enumerator = tiles.GetEnumerator();
            while (enumerator.MoveNext())
            {
                try
                {
                    var kvp = enumerator.Current;
                    var tile = kvp.Key;
                    var score = kvp.Value;
                    if (tile == null || score == null) continue;

                    var combined = score.GetScore();
                    if (float.IsInfinity(combined) || float.IsNaN(combined))
                        combined = combined > 0 ? 9999f : -9999f;

                    tileList.Add(new
                    {
                        x = tile.GetX(),
                        z = tile.GetZ(),
                        combined
                    });
                }
                catch { } // skip individual tile on error
            }

            if (tileList.Count == 0) return;

            // Gather all alive units from all factions for overlay
            var unitList = new System.Collections.Generic.List<object>();
            try
            {
                var allActors = EntitySpawner.ListEntities(-1);
                if (allActors != null)
                {
                    foreach (var a in allActors)
                    {
                        var aInfo = EntitySpawner.GetEntityInfo(a);
                        if (aInfo == null || !aInfo.IsAlive) continue;
                        var aPos = EntityMovement.GetPosition(a);
                        if (aPos == null) continue;
                        var aGo = new GameObj(a.Pointer);
                        var aTpl = aGo.ReadObj("m_Template");
                        var templateName = aTpl.IsNull ? "" : (aTpl.GetName() ?? "");

                        // Try to get leader nickname (character name like "rewa", "exconde")
                        var leaderName = "";
                        try
                        {
                            var unitActor = new UnitActor(a.Pointer);
                            var leader = unitActor.GetLeader();
                            if (leader != null)
                            {
                                var nickname = leader.GetNickname();
                                if (nickname != null)
                                    leaderName = nickname.GetTranslated() ?? "";
                            }
                        }
                        catch { }

                        unitList.Add(new
                        {
                            faction = aInfo.FactionIndex,
                            x = aPos.Value.x,
                            z = aPos.Value.y,
                            actor = BoamBridge.GetUuid(aInfo.EntityId),
                            name = templateName,
                            leader = leaderName
                        });
                    }
                }
            }
            catch { }

            // Vision range for overlay
            int visionRange = 0;
            try { visionRange = LineOfSight.GetVision(gameObj); } catch { }

            int round = BoamBridge.Instance?.Round ?? 0;

            var payload = JsonSerializer.Serialize(new
            {
                hook = "tile-scores",
                round,
                faction = factionId,
                actor = actorUuid,
                actorPosition = new { x = actorX, z = actorZ },
                tiles = tileList,
                units = unitList,
                visionRange
            });

            var response = BoamBridge.EnginePost("/hook/tile-scores", payload);
            if (response != null)
            {
                BoamBridge.Log.Msg($"[BOAM] tile-scores f{factionId} {actorUuid}: {tileList.Count} tiles");
            }
        }
        catch (Exception ex)
        {
            BoamBridge.Log.Error($"[BOAM] tile-scores error: {ex.Message}");
        }
    }
}

/// <summary>
/// Harmony patch: intercepts TacticalManager.InvokeOnMovementFinished to capture
/// the actual tile where a unit stopped after moving (AP-limited).
/// Patched directly because SDK TacticalEventHooks fails to init ("TacticalManager type not found").
/// </summary>
[HarmonyPatch(typeof(Il2CppMenace.Tactical.TacticalManager), "InvokeOnMovementFinished")]
static class Patch_MovementFinished
{
    static void Postfix(object __instance, Actor _actor, Il2CppMenace.Tactical.Tile _to)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsReady) return;
            if (_actor == null || _to == null) return;

            var actorInfo = BoamBridge.GetActorInfo(_actor);
            if (actorInfo == null) return;
            var (_, factionId, entityId, _) = actorInfo.Value;
            var actorUuid = BoamBridge.GetUuid(entityId);
            int tileX = _to.GetX();
            int tileZ = _to.GetZ();

            var payload = JsonSerializer.Serialize(new
            {
                hook = "movement_finished",
                faction = factionId,
                actor = actorUuid,
                tile = new { x = tileX, z = tileZ }
            });

            BoamBridge.Log.Msg($"[BOAM] movement-finished {actorUuid} tile=({tileX},{tileZ})");
            BoamBridge.EnginePost("/hook/movement-finished", payload);
        }
        catch (Exception ex)
        {
            BoamBridge.Log.Error($"[BOAM] movement-finished error: {ex.Message}");
        }
    }
}

/// <summary>
/// Harmony patch: intercepts Agent.Execute to capture AI behavior decisions.
/// By this point, PickBehavior() has run and m_ActiveBehavior is set.
/// We log the chosen behavior + all alternatives with their scores.
/// </summary>
[HarmonyPatch(typeof(Agent), nameof(Agent.Execute))]
static class Patch_AgentExecute
{
    static void Prefix(Agent __instance)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsReady) return;

            var actor = __instance.m_Actor;
            if (actor == null) return;

            var active = __instance.m_ActiveBehavior;
            if (active == null) return;

            var info = BoamBridge.GetActorInfo(actor);
            if (info == null) return;
            var (gameObj, factionId, actorEntityId, _) = info.Value;
            var actorUuid = BoamBridge.GetUuid(actorEntityId);

            // Build chosen behavior info
            var chosenId = (int)active.GetID();
            var chosenName = active.GetName() ?? active.GetID().ToString();
            var chosenScore = active.GetScore();

            // Try to get target tile for Move or SkillBehavior
            object target = null;
            try
            {
                var moveBehavior = active.TryCast<Move>();
                if (moveBehavior != null)
                {
                    var targetTile = moveBehavior.GetTargetTile();
                    if (targetTile?.Tile != null)
                    {
                        target = new
                        {
                            x = targetTile.Tile.GetX(),
                            z = targetTile.Tile.GetZ(),
                            apCost = targetTile.APCost
                        };
                    }
                }
            }
            catch { }

            if (target == null)
            {
                try
                {
                    var skillBehavior = active.TryCast<Il2CppMenace.Tactical.AI.SkillBehavior>();
                    if (skillBehavior != null && skillBehavior.m_TargetTile != null)
                    {
                        target = new
                        {
                            x = skillBehavior.m_TargetTile.GetX(),
                            z = skillBehavior.m_TargetTile.GetZ(),
                            apCost = 0
                        };
                    }
                }
                catch { }
            }

            // Try to extract attack candidates (target tiles + scores) for Attack behaviors
            object attackCandidates = null;
            try
            {
                var attackBehavior = active.TryCast<Attack>();
                if (attackBehavior != null)
                {
                    var candidates = attackBehavior.m_Candidates;
                    if (candidates != null && candidates.Count > 0)
                    {
                        var candList = new System.Collections.Generic.List<object>();
                        for (int i = 0; i < candidates.Count; i++)
                        {
                            try
                            {
                                var cand = candidates[i];
                                if (cand.Target == null) continue;
                                candList.Add(new
                                {
                                    x = cand.Target.GetX(),
                                    z = cand.Target.GetZ(),
                                    score = cand.Score
                                });
                            }
                            catch { }
                        }
                        if (candList.Count > 0)
                            attackCandidates = candList;
                    }
                }
            }
            catch { }

            // Build alternatives list from all behaviors
            var alternatives = new System.Collections.Generic.List<object>();
            try
            {
                var behaviors = __instance.GetBehaviors();
                if (behaviors != null)
                {
                    for (int i = 0; i < behaviors.Count; i++)
                    {
                        var b = behaviors[i];
                        if (b == null) continue;
                        alternatives.Add(new
                        {
                            behaviorId = (int)b.GetID(),
                            name = b.GetName() ?? b.GetID().ToString(),
                            score = b.GetScore()
                        });
                    }
                }
            }
            catch { }

            int round = bridge.Round;

            var payload = JsonSerializer.Serialize(new
            {
                hook = "action-decision",
                round,
                faction = factionId,
                actor = actorUuid,
                chosen = new
                {
                    behaviorId = chosenId,
                    name = chosenName,
                    score = chosenScore
                },
                target,
                alternatives,
                attackCandidates
            });

            BoamBridge.Log.Msg($"[BOAM] action-decision {actorUuid}: {chosenName}({chosenScore})");
            BoamBridge.EnginePost("/hook/action-decision", payload);
        }
        catch (Exception ex)
        {
            BoamBridge.Log.Error($"[BOAM] action-decision error: {ex.Message}");
        }
    }
}

/// <summary>
/// Harmony patch: intercepts TacticalState.EndTurn to log player endturn actions.
/// Registered manually in OnInitialize (type found via GameType.Find, same as TacticalController).
/// Uses Prefix so the active actor is still set when we read it.
/// </summary>
static class Patch_EndTurn
{
    // Guard: track last actor+round to prevent duplicate logging
    // (game calls EndTurn twice for the last player unit in a faction phase)
    private static int _lastActorId = -1;
    private static int _lastRound = -1;

    public static void Prefix()
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsReady) return;

            // Only log during player faction turns
            int factionId = TacticalController.GetCurrentFaction();
            if (factionId != (int)Menace.SDK.FactionType.Player
                && factionId != (int)Menace.SDK.FactionType.PlayerAI) return;

            // Get the active actor before EndTurn clears it
            var activeGameObj = TacticalController.GetActiveActor();
            if (activeGameObj.IsNull) return;

            var actor = new Actor(activeGameObj.Pointer);
            var actorInfo = BoamBridge.GetActorInfo(actor);
            if (actorInfo == null) return;
            var (gameObj, _, entityId, _) = actorInfo.Value;
            var actorUuid = BoamBridge.GetUuid(entityId);

            // Skip duplicate: same actor+round means EndTurn fired twice
            int round = bridge.Round;
            if (entityId == _lastActorId && round == _lastRound) return;
            _lastActorId = entityId;
            _lastRound = round;

            var (tileX, tileZ) = BoamBridge.GetPos(gameObj);

            var payload = JsonSerializer.Serialize(new
            {
                hook = "player-action",
                round,
                faction = factionId,
                actor = actorUuid,
                actionType = "endturn",
                skillName = "",
                tile = new { x = tileX, z = tileZ }
            });

            BoamBridge.Log.Msg($"[BOAM] player-action {actorUuid}: endturn at ({tileX},{tileZ})");
            BoamBridge.EnginePost("/hook/player-action", payload);
        }
        catch (Exception ex)
        {
            BoamBridge.Log.Error($"[BOAM] endturn patch error: {ex.Message}");
        }
    }
}


/// <summary>
/// Harmony patch: fires when the mission preview map finishes loading.
/// Notifies the tactical engine so event-driven navigation knows planmission is ready.
/// </summary>
static class Patch_PreviewReady
{
    public static void Postfix()
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null) return;
            BoamBridge.Log.Msg("[BOAM] Mission preview ready");
            if (bridge.IsReady || true) // always send — engine might not be "ready" yet during navigation
                ThreadPool.QueueUserWorkItem(_ => BoamBridge.EnginePost("/hook/preview-ready", "{}"));
        }
        catch { }
    }
}

/// <summary>
/// Harmony patch: fires when the active actor changes.
/// Sends actor info to the tactical engine for event-driven replay and logging.
/// </summary>
static class Patch_ActiveActorChanged
{
    public static void Postfix(object __instance, Il2CppMenace.Tactical.Actor _activeActor)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsReady) return;

            if (_activeActor == null)
            {
                ThreadPool.QueueUserWorkItem(_ => BoamBridge.EnginePost("/hook/actor-changed",
                    JsonSerializer.Serialize(new { actor = "", faction = 0, x = 0, z = 0 })));
                return;
            }

            var actorInfo = BoamBridge.GetActorInfo(_activeActor);
            if (actorInfo == null) return;
            var (gameObj, factionId, entityId, _) = actorInfo.Value;
            var actorUuid = BoamBridge.GetUuid(entityId);
            var (px, pz) = BoamBridge.GetPos(gameObj);

            var round = bridge.Round;
            var payload = JsonSerializer.Serialize(new
            {
                actor = actorUuid,
                faction = factionId,
                round,
                x = px,
                z = pz
            });

            BoamBridge.Log.Msg($"[BOAM] active-actor-changed: {actorUuid} r={round} at ({px},{pz})");
            ThreadPool.QueueUserWorkItem(_ => BoamBridge.EnginePost("/hook/actor-changed", payload));

            // Log select primitive for player factions
            if (factionId == 1 || factionId == 2)
            {
                var selectPayload = JsonSerializer.Serialize(new
                {
                    hook = "player-action",
                    round = bridge?.Round ?? 0,
                    faction = factionId,
                    actor = actorUuid,
                    actionType = "select",
                    skillName = "",
                    tile = new { x = px, z = pz }
                });
                BoamBridge.Log.Msg($"[BOAM] player-action {actorUuid}: select");
                ThreadPool.QueueUserWorkItem(_ => BoamBridge.EnginePost("/hook/player-action", selectPayload));
            }
        }
        catch { }
    }
}

/// <summary>
/// Harmony patch: logs click primitives when the player (or replay) clicks a tile.
/// Patched on multiple TacticalAction subclasses to capture all clicks.
/// Logs the concrete action type for diagnostics.
/// </summary>
static class Patch_ClickOnTile
{
    public static void Postfix(object __instance, Il2CppMenace.Tactical.Tile _tile, Actor _activeActor)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsReady) return;

            // Get the concrete action type name for diagnostics
            var actionClassName = __instance?.GetType()?.Name ?? "unknown";

            // Only log during player turns
            int factionId = TacticalController.GetCurrentFaction();
            if (factionId != 1 && factionId != 2) return;

            // Read tile coordinates from the parameter (what HandleLeftClickOnTile received)
            int tileX = 0, tileZ = 0;
            try { if (_tile != null) { tileX = _tile.GetX(); tileZ = _tile.GetZ(); } } catch { }

            string actorUuid = "";
            if (_activeActor != null)
            {
                var info = BoamBridge.GetActorInfo(_activeActor);
                if (info.HasValue) actorUuid = BoamBridge.GetUuid(info.Value.entityId);
            }

            // DIAGNOSTIC: log the action class that handled this click
            BoamBridge.Log.Msg($"[BOAM] CLICK-DIAG action={actionClassName} actor={actorUuid} tile=({tileX},{tileZ})");

            var payload = JsonSerializer.Serialize(new
            {
                hook = "player-action",
                round = bridge.Round,
                faction = factionId,
                actor = actorUuid,
                actionType = "click",
                skillName = "",
                tile = new { x = tileX, z = tileZ }
            });

            BoamBridge.Log.Msg($"[BOAM] player-action {actorUuid}: click ({tileX},{tileZ})");
            BoamBridge.EnginePost("/hook/player-action", payload);
        }
        catch { }
    }
}

/// <summary>
/// Harmony patch: logs useskill primitives when a skill is selected.
/// </summary>
static class Patch_SelectSkill
{
    public static void Postfix(object __instance, Il2CppMenace.Tactical.Skills.Skill _skill, bool __result)
    {
        try
        {
            if (!__result) return; // skill selection failed

            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsReady) return;

            int factionId = TacticalController.GetCurrentFaction();
            if (factionId != 1 && factionId != 2) return;

            var skillName = "";
            try { skillName = _skill?.GetTitle() ?? ""; } catch { }

            string actorUuid = "";
            var activeActor = TacticalController.GetActiveActor();
            if (!activeActor.IsNull)
            {
                var info = BoamBridge.GetActorInfo(new Actor(activeActor.Pointer));
                if (info.HasValue) actorUuid = BoamBridge.GetUuid(info.Value.entityId);
            }

            var payload = JsonSerializer.Serialize(new
            {
                hook = "player-action",
                round = bridge.Round,
                faction = factionId,
                actor = actorUuid,
                actionType = "useskill",
                skillName,
                tile = new { x = 0, z = 0 }
            });

            BoamBridge.Log.Msg($"[BOAM] player-action {actorUuid}: useskill {skillName}");
            BoamBridge.EnginePost("/hook/player-action", payload);
        }
        catch { }
    }
}

/// <summary>
/// Diagnostic patches for tracing turn/skill lifecycle during replay.
/// </summary>
static class Patch_Diagnostics
{
    public static void OnTurnEnd(Actor _actor)
    {
        try
        {
            var info = BoamBridge.GetActorInfo(_actor);
            var uuid = info.HasValue ? BoamBridge.GetUuid(info.Value.entityId) : "null";
            BoamBridge.Log.Msg($"[BOAM] DIAG TurnEnd: {uuid}");
        }
        catch { }
    }

    public static void OnAfterSkillUse(Il2CppMenace.Tactical.Skills.Skill _skill)
    {
        try
        {
            var name = _skill?.GetTitle() ?? "null";
            BoamBridge.Log.Msg($"[BOAM] DIAG AfterSkillUse: {name}");
        }
        catch { }
    }

    public static void OnAttackTileStart(Actor _actor, Il2CppMenace.Tactical.Skills.Skill _skill, Il2CppMenace.Tactical.Tile _targetTile, float _attackDurationInSec)
    {
        try
        {
            BoamBridge.SkillAnimationEndTime = UnityEngine.Time.time + _attackDurationInSec + 0.5f;
            var info = BoamBridge.GetActorInfo(_actor);
            var uuid = info.HasValue ? BoamBridge.GetUuid(info.Value.entityId) : "null";
            var skillName = _skill?.GetTitle() ?? "null";
            int tx = _targetTile?.GetX() ?? 0;
            int tz = _targetTile?.GetZ() ?? 0;
            BoamBridge.Log.Msg($"[BOAM] DIAG AttackStart: {uuid} {skillName} → ({tx},{tz}) duration={_attackDurationInSec}s");
        }
        catch { }
    }

    public static void OnActionPointsChanged(Actor _actor, int _oldAP, int _newAP)
    {
        try
        {
            var info = BoamBridge.GetActorInfo(_actor);
            var uuid = info.HasValue ? BoamBridge.GetUuid(info.Value.entityId) : "null";
            BoamBridge.Log.Msg($"[BOAM] DIAG AP: {uuid} {_oldAP} → {_newAP}");
        }
        catch { }
    }
}
