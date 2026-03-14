using System;
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
        if (!_inTactical || _ready) return;
        if (_initDelay > 0) { _initDelay--; return; }
        _ready = true;
        Log.Msg("[BOAM] Tactical ready, engine hooks active");
        if (_engineAvailable)
        {
            // Collect roster on main thread (safe — no concurrent access)
            var roster = BuildRoster();
            var payload = JsonSerializer.Serialize(new { roster });
            ThreadPool.QueueUserWorkItem(_ => EnginePost("/hook/tactical-ready", payload));
        }
    }

    /// Build the full actor roster on the main thread.
    private static System.Collections.Generic.List<object> BuildRoster()
    {
        var result = new System.Collections.Generic.List<object>();
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

                result.Add(new
                {
                    entityId = info.EntityId,
                    template = templateName,
                    faction = info.FactionIndex,
                    leader = leaderName.ToLowerInvariant(),
                    x = pos?.x ?? 0,
                    z = pos?.y ?? 0,
                    isAlive = info.IsAlive
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[BOAM] BuildRoster error: {ex.Message}");
        }
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
                        var (gameObj, _, entityId, templateName) = actorInfo.Value;
                        var (px, pz) = BoamBridge.GetPos(gameObj);

                        oppList.Add(new
                        {
                            actorId = entityId,
                            templateName,
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
            var (gameObj, factionId, actorEntityId, actorName) = info.Value;
            if (string.IsNullOrEmpty(actorName)) actorName = $"actor{actorEntityId}";

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
                actorId = actorEntityId,
                actorName,
                actorPosition = new { x = actorX, z = actorZ },
                tiles = tileList,
                units = unitList,
                visionRange
            });

            var response = BoamBridge.EnginePost("/hook/tile-scores", payload);
            if (response != null)
            {
                BoamBridge.Log.Msg($"[BOAM] tile-scores f{factionId} {actorName}: {tileList.Count} tiles");
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
            var (_, factionId, actorId, _) = actorInfo.Value;
            int tileX = _to.GetX();
            int tileZ = _to.GetZ();

            var payload = JsonSerializer.Serialize(new
            {
                hook = "movement-finished",
                faction = factionId,
                actorId,
                tile = new { x = tileX, z = tileZ }
            });

            BoamBridge.Log.Msg($"[BOAM] movement-finished f{factionId} actor={actorId} tile=({tileX},{tileZ})");
            BoamBridge.EnginePost("/hook/movement-finished", payload);

            // Also log as player action if this is a player faction unit
            // Skip if this movement was a disembark (already logged by Patch_Movement)
            if (factionId == 1 || factionId == 2)
            {
                if (Patch_Movement._lastDisembarkActorId == actorId)
                {
                    Patch_Movement._lastDisembarkActorId = -1;
                    // Skip — disembark already logged
                }
                else
                {
                    var (_, _, _, templateName) = actorInfo.Value;
                    var playerPayload = JsonSerializer.Serialize(new
                    {
                        hook = "player-action",
                        round = BoamBridge.Instance?.Round ?? 0,
                        faction = factionId,
                        actorId,
                        actorName = templateName,
                        actionType = "move",
                        skillName = "",
                        tile = new { x = tileX, z = tileZ }
                    });
                    BoamBridge.EnginePost("/hook/player-action", playerPayload);
                }
            }
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
            var (gameObj, factionId, actorEntityId, actorName) = info.Value;

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
                actorId = actorEntityId,
                actorName,
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

            BoamBridge.Log.Msg($"[BOAM] action-decision f{factionId} {actorName}: {chosenName}({chosenScore})");
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
            var (gameObj, _, actorId, templateName) = actorInfo.Value;

            // Skip duplicate: same actor+round means EndTurn fired twice
            int round = bridge.Round;
            if (actorId == _lastActorId && round == _lastRound) return;
            _lastActorId = actorId;
            _lastRound = round;

            var (tileX, tileZ) = BoamBridge.GetPos(gameObj);

            var payload = JsonSerializer.Serialize(new
            {
                hook = "player-action",
                round,
                faction = factionId,
                actorId,
                actorName = templateName,
                actionType = "endturn",
                skillName = "",
                tile = new { x = tileX, z = tileZ }
            });

            BoamBridge.Log.Msg($"[BOAM] player-action f{factionId} {templateName}: endturn at ({tileX},{tileZ})");
            BoamBridge.EnginePost("/hook/player-action", payload);
        }
        catch (Exception ex)
        {
            BoamBridge.Log.Error($"[BOAM] endturn patch error: {ex.Message}");
        }
    }
}

/// <summary>
/// Harmony patch: intercepts TacticalManager.InvokeOnSkillUse to capture player skill usage.
/// Fires for both AI and player — we filter to player factions only.
/// </summary>
[HarmonyPatch(typeof(Il2CppMenace.Tactical.TacticalManager), "InvokeOnSkillUse")]
static class Patch_OnSkillUse
{
    static void Postfix(Il2CppMenace.Tactical.TacticalManager __instance, Actor _actor, Il2CppMenace.Tactical.Skills.Skill _skill, Il2CppMenace.Tactical.Tile _targetTile)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsReady) return;
            if (_actor == null) return;

            var actorInfo = BoamBridge.GetActorInfo(_actor);
            if (actorInfo == null) return;
            var (_, factionId, actorId, templateName) = actorInfo.Value;

            // Only log player faction skill usage
            if (factionId != 1 && factionId != 2) return;

            var skillName = "";
            try { skillName = _skill?.GetTitle() ?? ""; } catch { }

            int tileX = 0, tileZ = 0;
            try
            {
                if (_targetTile != null) { tileX = _targetTile.GetX(); tileZ = _targetTile.GetZ(); }
            }
            catch { }

            var payload = JsonSerializer.Serialize(new
            {
                hook = "player-action",
                round = bridge.Round,
                faction = factionId,
                actorId,
                actorName = templateName,
                actionType = "skill",
                skillName,
                tile = new { x = tileX, z = tileZ }
            });

            BoamBridge.Log.Msg($"[BOAM] player-action f{factionId} {templateName}: skill={skillName} tile=({tileX},{tileZ})");
            BoamBridge.EnginePost("/hook/player-action", payload);
        }
        catch (Exception ex)
        {
            BoamBridge.Log.Error($"[BOAM] player-action error: {ex.Message}");
        }
    }
}

/// <summary>
/// Harmony patch: intercepts TacticalManager.InvokeOnMovement to capture embark/disembark events.
/// MovementAction.Enter (1) = entering a container, MovementAction.Leave (2) = leaving a container.
/// Sets _lastDisembarkActorId so Patch_MovementFinished can skip the duplicate player_move log.
/// </summary>
[HarmonyPatch(typeof(Il2CppMenace.Tactical.TacticalManager), "InvokeOnMovement")]
static class Patch_Movement
{
    internal static int _lastDisembarkActorId = -1;

    static void Postfix(object __instance, Actor _actor, Il2CppMenace.Tactical.Tile _from,
        Il2CppMenace.Tactical.Tile _to, Il2CppMenace.Tactical.MovementAction _action,
        Il2CppMenace.Tactical.Entity _container)
    {
        try
        {
            bool isEnter = (_action & Il2CppMenace.Tactical.MovementAction.Enter) != 0;
            bool isLeave = (_action & Il2CppMenace.Tactical.MovementAction.Leave) != 0;
            if (!isEnter && !isLeave) return;

            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsReady) return;
            if (_actor == null) return;

            var actorInfo = BoamBridge.GetActorInfo(_actor);
            if (actorInfo == null) return;
            var (_, factionId, actorId, templateName) = actorInfo.Value;

            // Only log for player factions
            if (factionId != 1 && factionId != 2) return;

            if (isEnter && _container != null)
            {
                // Embark: actor entering a container
                var containerObj = new GameObj(_container.Pointer);
                var containerInfo = EntitySpawner.GetEntityInfo(containerObj);
                int containerId = containerInfo?.EntityId ?? 0;
                int containerX = _to != null ? _to.GetX() : 0;
                int containerZ = _to != null ? _to.GetZ() : 0;

                var payload = JsonSerializer.Serialize(new
                {
                    hook = "player-action",
                    round = bridge.Round,
                    faction = factionId,
                    actorId,
                    actorName = templateName,
                    actionType = "embark",
                    skillName = "",
                    tile = new { x = containerX, z = containerZ },
                    vehicleId = containerId
                });

                BoamBridge.Log.Msg($"[BOAM] player-action f{factionId} {templateName}: embark into vehicle {containerId} at ({containerX},{containerZ})");
                BoamBridge.EnginePost("/hook/player-action", payload);
            }
            else if (isLeave)
            {
                // Disembark: actor leaving a container — suppress duplicate player_move from MovementFinished
                _lastDisembarkActorId = actorId;
                int tileX = _to != null ? _to.GetX() : 0;
                int tileZ = _to != null ? _to.GetZ() : 0;

                // Get container ID — _container may be null on Leave, try actor's current container
                int containerId = 0;
                if (_container != null)
                {
                    var containerObj = new GameObj(_container.Pointer);
                    var containerInfo = EntitySpawner.GetEntityInfo(containerObj);
                    containerId = containerInfo?.EntityId ?? 0;
                }

                var payload = JsonSerializer.Serialize(new
                {
                    hook = "player-action",
                    round = bridge.Round,
                    faction = factionId,
                    actorId,
                    actorName = templateName,
                    actionType = "disembark",
                    skillName = "",
                    tile = new { x = tileX, z = tileZ },
                    vehicleId = containerId
                });

                BoamBridge.Log.Msg($"[BOAM] player-action f{factionId} {templateName}: disembark to ({tileX},{tileZ})");
                BoamBridge.EnginePost("/hook/player-action", payload);
            }
        }
        catch (Exception ex)
        {
            BoamBridge.Log.Error($"[BOAM] movement enter/leave error: {ex.Message}");
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
            if (bridge == null) return;

            if (_activeActor == null)
            {
                ThreadPool.QueueUserWorkItem(_ => BoamBridge.EnginePost("/hook/actor-changed",
                    JsonSerializer.Serialize(new { actorId = 0, template = "", faction = 0, x = 0, z = 0 })));
                return;
            }

            var actorInfo = BoamBridge.GetActorInfo(_activeActor);
            if (actorInfo == null) return;
            var (gameObj, factionId, entityId, templateName) = actorInfo.Value;
            var (px, pz) = BoamBridge.GetPos(gameObj);

            var payload = JsonSerializer.Serialize(new
            {
                actorId = entityId,
                template = templateName,
                faction = factionId,
                x = px,
                z = pz
            });

            BoamBridge.Log.Msg($"[BOAM] active-actor-changed: {templateName} (ID:{entityId}) at ({px},{pz})");
            ThreadPool.QueueUserWorkItem(_ => BoamBridge.EnginePost("/hook/actor-changed", payload));
        }
        catch { }
    }
}
