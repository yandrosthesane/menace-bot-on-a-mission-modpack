using System;
using HarmonyLib;
using Il2CppMenace.Tactical.AI;

namespace BOAM;

/// <summary>
/// Applies engine-computed per-tile utility modifiers during PostProcessTileScores.
/// Pure lookup — all scoring logic lives in the F# engine.
/// </summary>
[HarmonyPatch(typeof(Agent), "PostProcessTileScores")]
static class TileModifierPatch
{
    static void Postfix(Agent __instance)
    {
        if (!DataEvents.TileModifiersEvent.IsActive) return;
        try
        {
            var actor = __instance.m_Actor;
            if (actor == null) return;

            var actorInfo = ActorRegistry.GetActorInfo(actor);
            if (actorInfo == null) return;
            var uuid = ActorRegistry.GetUuid(actorInfo.Value.Item3);

            if (!TileModifierStore.TryGet(uuid, out var tileMap)) return;
            if (tileMap.Count == 0) return;

            var tiles = __instance.m_Tiles;
            if (tiles == null || tiles.Count == 0) return;

            int applied = 0;
            var enumerator = tiles.GetEnumerator();
            while (enumerator.MoveNext())
            {
                var kvp = enumerator.Current;
                var tile = kvp.Key;
                var score = kvp.Value;
                if (tile == null || score == null) continue;

                if (tileMap.TryGetValue((tile.GetX(), tile.GetZ()), out var mod))
                {
                    score.UtilityScore += mod.Utility;
                    score.UtilityScoreScaled += mod.Utility;
                    score.SafetyScore += mod.Safety;
                    score.SafetyScoreScaled += mod.Safety;
                    score.DistanceScore += mod.Distance;
                    score.UtilityByAttacksScore += mod.UtilityByAttacks;
                    applied++;
                }
            }

            if (applied > 0)
                BoamBridge.Logger?.Msg($"[BOAM] TileModifier {uuid}: applied {applied}/{tileMap.Count} tiles");
        }
        catch (Exception ex)
        {
            BoamBridge.Logger?.Error($"[BOAM] TileModifierPatch error: {ex.Message}");
        }
    }
}
