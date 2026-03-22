using System;
using HarmonyLib;
using Il2CppMenace.Tactical.AI;

namespace BOAM;

/// <summary>
/// Applies engine-defined tile modifiers during PostProcessTileScores.
/// Reads from TileModifierStore — the engine populates it via the command server.
/// </summary>
[HarmonyPatch(typeof(Agent), "PostProcessTileScores")]
static class TileModifierPatch
{
    static void Postfix(Agent __instance)
    {
        try
        {
            var actor = __instance.m_Actor;
            if (actor == null) return;

            var actorInfo = ActorRegistry.GetActorInfo(actor);
            if (actorInfo == null) return;
            var uuid = ActorRegistry.GetUuid(actorInfo.Value.Item3);

            if (!TileModifierStore.TryGet(uuid, out var mod)) return;
            BoamBridge.Logger?.Msg($"[BOAM] TileModifier applying to {uuid}: target=({mod.TargetX},{mod.TargetZ}) utility={mod.AddUtility}");

            var tiles = __instance.m_Tiles;
            if (tiles == null || tiles.Count == 0) return;

            bool hasTarget = mod.TargetX >= 0 && mod.TargetZ >= 0;

            // For target mode, compute current distance to target
            float currentDistToTarget = 0f;
            if (hasTarget)
            {
                var (gameObj, _, _, _) = actorInfo.Value;
                var (curX, curZ) = ActorRegistry.GetPos(gameObj);
                float dx = mod.TargetX - curX;
                float dz = mod.TargetZ - curZ;
                currentDistToTarget = (float)System.Math.Sqrt(dx * dx + dz * dz);

                // Already on target — don't boost any tile, BehaviorOverridePatch handles idle
                if (currentDistToTarget < 0.5f) return;
            }

            var enumerator = tiles.GetEnumerator();
            while (enumerator.MoveNext())
            {
                var kvp = enumerator.Current;
                var tile = kvp.Key;
                var score = kvp.Value;
                if (tile == null || score == null) continue;

                if (hasTarget)
                {
                    // Target mode: bonus proportional to how much closer this tile is to the target
                    float dx = mod.TargetX - tile.GetX();
                    float dz = mod.TargetZ - tile.GetZ();
                    float tileDistToTarget = (float)System.Math.Sqrt(dx * dx + dz * dz);
                    float closerBy = currentDistToTarget - tileDistToTarget;
                    if (closerBy <= 0) continue; // not closer, skip

                    float bonus = mod.AddUtility * (closerBy / System.Math.Max(currentDistToTarget, 1f));
                    score.UtilityScore += bonus;
                    score.UtilityScoreScaled += bonus;
                }
                else
                {
                    // Distance gating mode
                    float dist = score.DistanceToCurrentTile;
                    if (mod.MinDistance > 0 && dist < mod.MinDistance) continue;
                    if (mod.MaxDistance > 0 && dist > mod.MaxDistance) continue;

                    if (mod.AddUtility != 0f)
                    {
                        score.UtilityScore += mod.AddUtility;
                        score.UtilityScoreScaled += mod.AddUtility;
                    }
                }
            }


        }
        catch (Exception ex)
        {
            BoamBridge.Logger?.Error($"[BOAM] TileModifierPatch error: {ex.Message}");
        }
    }
}
