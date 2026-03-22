using System;
using HarmonyLib;
using Il2CppMenace.Tactical.AI;
using Il2CppMenace.Tactical.AI.Behaviors;

namespace BOAM;

/// <summary>
/// Overrides AI behavior selection based on tile modifiers.
/// Runs as a prefix on Agent.Execute — after PickBehavior has chosen m_ActiveBehavior,
/// but before it executes. Can force Idle or suppress attacks.
/// </summary>
[HarmonyPatch(typeof(Agent), nameof(Agent.Execute))]
static class BehaviorOverridePatch
{
    static void Prefix(Agent __instance)
    {
        try
        {
            var actor = __instance.m_Actor;
            if (actor == null) return;

            var actorInfo = ActorRegistry.GetActorInfo(actor);
            if (actorInfo == null) return;
            var uuid = ActorRegistry.GetUuid(actorInfo.Value.Item3);

            if (!TileModifierStore.TryGet(uuid, out var mod)) return;

            var active = __instance.m_ActiveBehavior;
            if (active == null) return;

            var activeName = active.GetName();

            // Suppress attack: if chosen behavior is not Move or Idle, force Idle
            if (mod.SuppressAttack && activeName != "Move" && activeName != "Idle")
            {
                ForceIdle(__instance);
                return;
            }

            // Force idle when on target tile
            if (mod.TargetX >= 0 && mod.TargetZ >= 0)
            {
                var (gameObj, _, _, _) = actorInfo.Value;
                var (curX, curZ) = ActorRegistry.GetPos(gameObj);
                float dx = mod.TargetX - curX;
                float dz = mod.TargetZ - curZ;
                if (dx * dx + dz * dz < 0.25f)
                {
                    ForceIdle(__instance);
                    return;
                }
            }
        }
        catch { }
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
