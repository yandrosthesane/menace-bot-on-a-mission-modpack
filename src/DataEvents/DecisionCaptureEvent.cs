namespace BOAM.DataEvents;

static class DecisionCaptureEvent
{
    internal static bool IsActive => Boundary.DataEvents.DecisionCapture;
}
