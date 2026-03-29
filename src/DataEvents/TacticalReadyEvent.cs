namespace BOAM.DataEvents;

static class TacticalReadyEvent
{
    internal static bool IsActive => Boundary.DataEvents.TacticalReady;
}
