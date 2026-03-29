using System.Text.Json;
using System.Threading;
using System.Collections.Generic;

namespace BOAM.GameEvents;

static class TacticalReadyEvent
{
    internal static bool IsActive => Boundary.GameEvents.TacticalReady;

    internal static void Process(List<object> dramatisPersonae)
    {
        if (!IsActive) return;
        var payload = JsonSerializer.Serialize(new { dramatis_personae = dramatisPersonae });
        ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("tactical-ready", payload));
    }
}
