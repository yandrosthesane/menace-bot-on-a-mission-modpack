namespace BOAM.DataEvents;

static class CombatLoggingEvent
{
    internal static bool IsActive => Boundary.DataEvents.CombatLogging;
}
