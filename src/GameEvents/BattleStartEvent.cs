namespace BOAM.GameEvents;

static class BattleStartEvent
{
    internal static bool IsActive => Boundary.GameEvents.BattleStart;
}
