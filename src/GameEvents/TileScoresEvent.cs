using System;
using System.Text.Json;
using System.Threading;
using HarmonyLib;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;
using Menace.SDK;

namespace BOAM.GameEvents;

static class TileScoresEvent
{
    internal static bool IsActive => Boundary.GameEvents.TileScores;
}

[HarmonyPatch(typeof(Agent), "PostProcessTileScores")]
static class Patch_PostProcessTileScores
{
    static void Postfix(Agent __instance)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsTacticalReady) return;
            if (!TileScoresEvent.IsActive && !MinimapUnitsEvent.IsActive) return;

            var actor = __instance.m_Actor;
            if (actor == null) return;

            var tiles = __instance.m_Tiles;
            if (tiles == null || tiles.Count == 0) return;

            var info = ActorRegistry.GetActorInfo(actor);
            if (info == null) return;
            var (gameObj, factionId, actorEntityId, _) = info.Value;
            var actorUuid = ActorRegistry.GetUuid(actorEntityId);
            var (actorX, actorZ) = ActorRegistry.GetPos(gameObj);

            // Tile enumeration for engine
            var tileList = new System.Collections.Generic.List<object>();
            if (TileScoresEvent.IsActive)
            {
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

                        if (bridge.CriterionLogging)
                        {
                            tileList.Add(new
                            {
                                x = tile.GetX(),
                                z = tile.GetZ(),
                                combined,
                                utility = score.UtilityScore,
                                utilityScaled = score.UtilityScoreScaled,
                                safety = score.SafetyScore,
                                safetyScaled = score.SafetyScoreScaled,
                                distance = score.DistanceScore,
                                distanceToCurrent = score.DistanceToCurrentTile,
                                apCost = score.APCost,
                                isVisible = score.IsVisibleToOpponentsHere,
                                utilityByAttacks = score.UtilityByAttacksScore
                            });
                        }
                        else
                        {
                            tileList.Add(new
                            {
                                x = tile.GetX(),
                                z = tile.GetZ(),
                                combined
                            });
                        }
                    }
                    catch { }
                }
            }

            // Unit list for heatmaps
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

            int visionRange = 0;
            try { visionRange = LineOfSight.GetVision(gameObj); } catch { }

            int round = BoamBridge.Instance?.Round ?? 0;

            // Minimap overlay
            MinimapUnitsEvent.PopulateOverlay(factionId, round);

            // Send to engine
            if (!bridge.IsEngineReady || !TileScoresEvent.IsActive) return;

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

            var json = payload;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var response = QueryCommandClient.Hook("tile-scores", json);
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
