namespace BOAM.DataEvents;

static class PreviewReadyEvent
{
    internal static bool IsActive => Boundary.DataEvents.PreviewReady;
}
