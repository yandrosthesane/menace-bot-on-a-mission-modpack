using System;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Il2CppMenace.Tactical;
using MelonLoader;
using Menace.SDK;

namespace BOAM.GameEvents;

static class ActorChangedEvent
{
    internal static bool IsActive => Boundary.GameEvents.ActorChanged;

    internal static void Register(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
    {
        try
        {
            var tsType = GameType.Find("Menace.States.TacticalState")?.ManagedType;
            if (tsType != null)
            {
                var m = tsType.GetMethod("OnActiveActorChanged", BindingFlags.Public | BindingFlags.Instance);
                if (m != null)
                {
                    harmony.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(ActorChangedEvent), nameof(OnActiveActorChanged)));
                    log.Msg("[BOAM] Patched TacticalState.OnActiveActorChanged");
                }
            }
        }
        catch (Exception ex)
        {
            log.Error($"[BOAM] Failed to patch OnActiveActorChanged: {ex.Message}");
        }
    }

    public static void OnActiveActorChanged(object __instance, Actor _activeActor)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsTacticalReady) return;

            if (_activeActor == null)
            {
                if (bridge.IsEngineReady && IsActive)
                    ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.SendEvent("actor-changed",
                        JsonSerializer.Serialize(new { actor = "", faction = 0, x = 0, z = 0 })));
                return;
            }

            var actorInfo = ActorRegistry.GetActorInfo(_activeActor);
            if (actorInfo == null) return;
            var (gameObj, factionId, entityId, _) = actorInfo.Value;
            var actorUuid = ActorRegistry.GetUuid(entityId);
            var (px, pz) = ActorRegistry.GetPos(gameObj);

            MinimapUnitsEvent.SetActiveActor(actorUuid, px, pz);

            if (!bridge.IsEngineReady || !IsActive) return;

            var round = bridge.Round;
            var payload = JsonSerializer.Serialize(new
            {
                actor = actorUuid, faction = factionId, round, x = px, z = pz
            });

            BoamBridge.Logger.Msg($"[BOAM] active-actor-changed: {actorUuid} r={round} at ({px},{pz})");
            ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.SendEvent("actor-changed", payload));

            // Log select primitive for player factions
            if (ActionLoggingEvent.IsActive && (factionId == 1 || factionId == 2))
            {
                var selectPayload = JsonSerializer.Serialize(new
                {
                    actionType = "select", skillName = "", tile = new { x = px, z = pz }
                });
                BoamBridge.Logger.Msg($"[BOAM] player-action {actorUuid}: select");
                ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.SendEvent("player-action", selectPayload));
            }
        }
        catch { }
    }
}
