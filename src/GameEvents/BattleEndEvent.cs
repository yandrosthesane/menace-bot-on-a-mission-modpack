using System.Threading;

namespace BOAM.GameEvents;

static class BattleEndEvent
{
    internal static bool IsActive => Boundary.GameEvents.BattleEnd;

    internal static void Process()
    {
        Boundary.GameStore.Clear();
        if (!IsActive) return;
        ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("battle-end", "{}"));
    }
}
