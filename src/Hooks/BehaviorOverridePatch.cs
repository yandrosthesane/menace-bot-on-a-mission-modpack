using System;
using HarmonyLib;
using Il2CppMenace.Tactical.AI;
using Il2CppMenace.Tactical.AI.Behaviors;

namespace BOAM;

/// <summary>
/// Overrides AI behavior selection based on engine directives.
/// Runs as a prefix on Agent.Execute — after PickBehavior has chosen m_ActiveBehavior,
/// but before it executes.
/// Currently a no-op — per-tile modifiers handle movement scoring directly.
/// </summary>
[HarmonyPatch(typeof(Agent), nameof(Agent.Execute))]
static class BehaviorOverridePatch
{
    static void Prefix(Agent __instance)
    {
        // Reserved for future behavior overrides (e.g., suppress attacks, force idle).
        // Per-tile utility modifiers now handle roaming via PostProcessTileScores.
    }

    private static void ForceIdle(Agent agent)
    {
        try
        {
            var behaviors = agent.GetBehaviors();
            if (behaviors == null) return;
            for (int i = 0; i < behaviors.Count; i++)
            {
                var b = behaviors[i];
                if (b != null && b.GetName() == "Idle")
                {
                    agent.m_ActiveBehavior = b;
                    return;
                }
            }
        }
        catch { }
    }
}
