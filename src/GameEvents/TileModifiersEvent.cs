using System;
using HarmonyLib;
using Il2CppMenace.Tactical.AI;

namespace BOAM.GameEvents;

static class TileModifiersEvent
{
    internal static bool IsActive => Boundary.GameEvents.TileModifiers;

    internal static void SetPending()
    {
        if (!IsActive) return;
        TileModifierStore.SetPending();
    }

    internal static void WaitReady()
    {
        if (!IsActive) return;
        TileModifierStore.WaitReady();
    }
}

[HarmonyPatch(typeof(Agent), "PostProcessTileScores")]
static class TileModifierPatch
{
    static void Postfix(Agent __instance)
    {
        if (!TileModifiersEvent.IsActive) return;
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

            // Find best tile before modifiers
            float bestBeforeScore = float.MinValue;
            int bestBeforeX = 0, bestBeforeZ = 0;
            float bestBeforeU = 0, bestBeforeS = 0, bestBeforeD = 0, bestBeforeA = 0;
            {
                var be = tiles.GetEnumerator();
                while (be.MoveNext())
                {
                    var s = be.Current.Value;
                    if (s != null && s.GetScore() > bestBeforeScore)
                    {
                        bestBeforeScore = s.GetScore();
                        bestBeforeX = be.Current.Key?.GetX() ?? 0;
                        bestBeforeZ = be.Current.Key?.GetZ() ?? 0;
                        bestBeforeU = s.UtilityScore; bestBeforeS = s.SafetyScore;
                        bestBeforeD = s.DistanceScore; bestBeforeA = s.UtilityByAttacksScore;
                    }
                }
            }

            // Apply additive modifiers
            int applied = 0;
            float chosenModU = 0, chosenModS = 0, chosenModD = 0, chosenModA = 0;
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
            {
                // Find best tile after modifiers
                float bestAfterScore = float.MinValue;
                int bestAfterX = 0, bestAfterZ = 0;
                float bestAfterU = 0, bestAfterS = 0, bestAfterD = 0, bestAfterA = 0;
                {
                    var ae = tiles.GetEnumerator();
                    while (ae.MoveNext())
                    {
                        var s = ae.Current.Value;
                        if (s != null && s.GetScore() > bestAfterScore)
                        {
                            bestAfterScore = s.GetScore();
                            bestAfterX = ae.Current.Key?.GetX() ?? 0;
                            bestAfterZ = ae.Current.Key?.GetZ() ?? 0;
                            bestAfterU = s.UtilityScore; bestAfterS = s.SafetyScore;
                            bestAfterD = s.DistanceScore; bestAfterA = s.UtilityByAttacksScore;
                        }
                    }
                }

                // Log modifier contribution on the chosen tile
                tileMap.TryGetValue((bestAfterX, bestAfterZ), out var chosenMod);
                chosenModU = chosenMod.Utility; chosenModS = chosenMod.Safety;
                chosenModD = chosenMod.Distance; chosenModA = chosenMod.UtilityByAttacks;

                var shifted = bestBeforeX != bestAfterX || bestBeforeZ != bestAfterZ;
                BoamBridge.Logger?.Msg($"[BOAM] TileModifier {uuid}: {applied}/{tileMap.Count} tiles" +
                    $"  before=({bestBeforeX},{bestBeforeZ}) U={bestBeforeU:F0} S={bestBeforeS:F0} D={bestBeforeD:F0} A={bestBeforeA:F0}" +
                    $"  after=({bestAfterX},{bestAfterZ}) U={bestAfterU:F0} S={bestAfterS:F0} D={bestAfterD:F0} A={bestAfterA:F0}" +
                    $"  mod=U{chosenModU:+0;-0;0} S{chosenModS:+0;-0;0} D{chosenModD:+0;-0;0} A{chosenModA:+0;-0;0}" +
                    (shifted ? " SHIFTED" : ""));
            }
        }
        catch (Exception ex)
        {
            BoamBridge.Logger?.Error($"[BOAM] TileModifierPatch error: {ex.Message}");
        }
    }
}
