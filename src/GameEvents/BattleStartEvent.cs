using System.Text.Json;
using System.Threading;

namespace BOAM.GameEvents;

static class BattleStartEvent
{
    internal static bool IsActive => Boundary.GameEvents.BattleStart;

    internal static void Process()
    {
        if (!IsActive) return;
        var sessionDir = TacticalMap.TacticalMapState.BattleSessionDir ?? "";
        var payload = JsonSerializer.Serialize(new { sessionDir });
        ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("battle-start", payload));
    }
}
