using System;
using System.Text.Json;
using HarmonyLib;
using Il2CppMenace.Tactical;

namespace BOAM.GameEvents;

static class MovementFinishedEvent
{
    internal static bool IsActive => Boundary.GameEvents.MovementFinished;
}

[HarmonyPatch(typeof(TacticalManager), "InvokeOnMovementFinished")]
static class Patch_MovementFinished
{
    static void Postfix(object __instance, Actor _actor, Tile _to)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsTacticalReady) return;
            if (_actor == null || _to == null) return;

            var actorInfo = ActorRegistry.GetActorInfo(_actor);
            if (actorInfo == null) return;
            var (_, factionId, entityId, _) = actorInfo.Value;
            var actorUuid = ActorRegistry.GetUuid(entityId);
            int tileX = _to.GetX();
            int tileZ = _to.GetZ();

            MinimapUnitsEvent.UpdatePosition(actorUuid, tileX, tileZ);

            if (!bridge.IsEngineReady || !MovementFinishedEvent.IsActive) return;

            var payload = JsonSerializer.Serialize(new
            {
                faction = factionId,
                actor = actorUuid,
                tile = new { x = tileX, z = tileZ }
            });

            BoamBridge.Logger.Msg($"[BOAM] movement-finished {actorUuid} tile=({tileX},{tileZ})");
            QueryCommandClient.SendEvent("movement-finished", payload);
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] movement-finished error: {ex.Message}");
        }
    }
}
