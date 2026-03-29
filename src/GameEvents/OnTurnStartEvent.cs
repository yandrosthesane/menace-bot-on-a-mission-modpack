using System;
using System.Text.Json;
using HarmonyLib;
using Il2CppMenace.Tactical.AI;

namespace BOAM.GameEvents;

static class OnTurnStartEvent
{
    internal static bool IsActive => Boundary.GameEvents.OnTurnStart;
}

[HarmonyPatch(typeof(AIFaction), nameof(AIFaction.OnTurnStart))]
static class Patch_OnTurnStart
{
    static void Prefix(AIFaction __instance)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsEngineReady) return;
            if (!OnTurnStartEvent.IsActive) return;

            TileModifiersEvent.WaitReady();

            int factionIdx = __instance.GetIndex();
            var (oppList, opponentCount) = OpponentTrackingEvent.Gather(__instance);
            int round = BoamBridge.Instance?.Round ?? 0;

            var payload = JsonSerializer.Serialize(new
            {
                round,
                faction = factionIdx,
                opponentCount,
                opponents = oppList
            });

            var response = QueryCommandClient.SendEvent("on-turn-start", payload);
            if (response != null)
                BoamBridge.Logger.Msg($"[BOAM] on-turn-start f{factionIdx} r{round}: {oppList.Count} opponents");
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] on-turn-start error: {ex.Message}");
        }
    }
}
