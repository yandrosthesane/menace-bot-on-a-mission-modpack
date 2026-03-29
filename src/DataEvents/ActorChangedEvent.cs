namespace BOAM.DataEvents;

static class ActorChangedEvent
{
    internal static bool IsActive => Boundary.DataEvents.ActorChanged;
}
