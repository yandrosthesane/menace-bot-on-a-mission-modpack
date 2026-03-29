namespace BOAM.GameEvents;

static class TacticalReadyEvent
{
    internal static bool IsActive => Boundary.GameEvents.TacticalReady;
}
