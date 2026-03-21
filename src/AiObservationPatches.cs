using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using HarmonyLib;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;
using Il2CppMenace.Tactical.AI.Behaviors;
using Il2CppMenace.Tactical.Skills;
using MelonLoader;
using Menace.SDK;

namespace BOAM;

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
                        var actorInfo = ActorRegistry.GetActorInfo(opp.Actor);
                        if (actorInfo == null) continue;
                        var (gameObj, _, entityId, _) = actorInfo.Value;
                        var (px, pz) = ActorRegistry.GetPos(gameObj);

                        oppList.Add(new
                        {
                            actor = ActorRegistry.GetUuid(entityId),
                            position = new { x = px, z = pz },
                            ttl = opp.TTL,
                            isKnown = opp.IsKnown(),
                            isAlive = opp.Actor.IsAlive()
                        });
                    }
                    catch { }
                }
            }

            int round = BoamBridge.Instance?.Round ?? 0;

            var payload = JsonSerializer.Serialize(new
            {
                hook = "on-turn-start",
                round,
                faction = factionIdx,
                opponentCount,
                opponents = oppList
            });

            var response = EngineClient.Post("/hook/on-turn-start", payload);
            if (response != null)
            {
                BoamBridge.Logger.Msg($"[BOAM] on-turn-start f{factionIdx} r{round}: engine OK ({response.Length}b, {oppList.Count} opponents)");
            }
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] on-turn-start error: {ex.Message}");
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

            var info = ActorRegistry.GetActorInfo(actor);
            if (info == null) return;
            var (gameObj, factionId, actorEntityId, _) = info.Value;
            var actorUuid = ActorRegistry.GetUuid(actorEntityId);

            var (actorX, actorZ) = ActorRegistry.GetPos(gameObj);

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
                            actor = ActorRegistry.GetUuid(aInfo.EntityId),
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

            // Update TacticalMapState with current unit positions for the minimap overlay
            var overlayUnits = new System.Collections.Generic.List<TacticalMap.OverlayUnit>();
            try
            {
                var allActors2 = EntitySpawner.ListEntities(-1);
                if (allActors2 != null)
                {
                    foreach (var a in allActors2)
                    {
                        var aInfo2 = EntitySpawner.GetEntityInfo(a);
                        if (aInfo2 == null || !aInfo2.IsAlive) continue;
                        var aPos2 = EntityMovement.GetPosition(a);
                        if (aPos2 == null) continue;

                        int visibility = Menace.SDK.LineOfSight.GetVisibilityState(a);
                        bool isPlayerSide = aInfo2.FactionIndex == 1 || aInfo2.FactionIndex == 2 || aInfo2.FactionIndex == 4;
                        bool knownToPlayer = isPlayerSide || visibility == 1 || visibility == 3;

                        var aGo2 = new GameObj(a.Pointer);
                        var aTpl2 = aGo2.ReadObj("m_Template");
                        var templateName2 = aTpl2.IsNull ? "" : (aTpl2.GetName() ?? "");

                        var leaderName2 = "";
                        try
                        {
                            var unitActor2 = new UnitActor(a.Pointer);
                            var leader2 = unitActor2.GetLeader();
                            if (leader2 != null)
                            {
                                var nn = leader2.GetNickname();
                                if (nn != null) leaderName2 = nn.GetTranslated() ?? "";
                            }
                        }
                        catch { }

                        var actorUuid2 = ActorRegistry.GetUuid(aInfo2.EntityId);

                        overlayUnits.Add(new TacticalMap.OverlayUnit
                        {
                            Actor = actorUuid2,
                            Label = actorUuid2,
                            FactionIndex = aInfo2.FactionIndex,
                            X = aPos2.Value.x,
                            Y = aPos2.Value.y,
                            KnownToPlayer = knownToPlayer,
                            Template = templateName2,
                            Leader = leaderName2
                        });
                    }
                }
            }
            catch { }
            TacticalMap.TacticalMapState.SetUnits(overlayUnits);
            TacticalMap.TacticalMapState.CurrentRound = round;
            TacticalMap.TacticalMapState.CurrentFaction = factionId;

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

            // Fire-and-forget: don't block AI evaluation thread
            var json = payload;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var response = EngineClient.Post("/hook/tile-scores", json);
                if (response != null)
                    BoamBridge.Logger.Msg($"[BOAM] tile-scores f{factionId} {actorUuid}: {tileList.Count} tiles");
            });
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] tile-scores error: {ex.Message}");
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

            var actorInfo = ActorRegistry.GetActorInfo(_actor);
            if (actorInfo == null) return;
            var (_, factionId, entityId, _) = actorInfo.Value;
            var actorUuid = ActorRegistry.GetUuid(entityId);
            int tileX = _to.GetX();
            int tileZ = _to.GetZ();

            var payload = JsonSerializer.Serialize(new
            {
                hook = "movement_finished",
                faction = factionId,
                actor = actorUuid,
                tile = new { x = tileX, z = tileZ }
            });

            // Update minimap overlay with new position
            TacticalMap.TacticalMapState.UpdateUnitPosition(actorUuid, tileX, tileZ);

            BoamBridge.Logger.Msg($"[BOAM] movement-finished {actorUuid} tile=({tileX},{tileZ})");
            EngineClient.Post("/hook/movement-finished", payload);
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] movement-finished error: {ex.Message}");
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

            // Logging only — capture AI behavior decisions for analysis.

            var actor = __instance.m_Actor;
            if (actor == null) return;

            var active = __instance.m_ActiveBehavior;
            if (active == null) return;

            var info = ActorRegistry.GetActorInfo(actor);
            if (info == null) return;
            var (gameObj, factionId, actorEntityId, _) = info.Value;
            var actorUuid = ActorRegistry.GetUuid(actorEntityId);

            int round = bridge.Round;

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

            BoamBridge.Logger.Msg($"[BOAM] action-decision {actorUuid}: {chosenName}({chosenScore})");
            EngineClient.Post("/hook/action-decision", payload);
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] action-decision error: {ex.Message}");
        }
    }
}
