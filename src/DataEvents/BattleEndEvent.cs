namespace BOAM.DataEvents;

static class BattleEndEvent
{
    internal static bool IsActive => Boundary.DataEvents.BattleEnd;
}
