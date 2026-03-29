namespace BOAM.DataEvents;

static class BattleStartEvent
{
    internal static bool IsActive => Boundary.DataEvents.BattleStart;
}
