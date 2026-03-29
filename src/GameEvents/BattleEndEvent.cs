namespace BOAM.GameEvents;

static class BattleEndEvent
{
    internal static bool IsActive => Boundary.GameEvents.BattleEnd;
}
