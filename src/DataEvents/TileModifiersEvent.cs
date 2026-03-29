namespace BOAM.DataEvents;

static class TileModifiersEvent
{
    internal static void SetPending()
    {
        if (!Boundary.DataEvents.TileModifiers) return;
        TileModifierStore.SetPending();
    }

    internal static void WaitReady()
    {
        if (!Boundary.DataEvents.TileModifiers) return;
        TileModifierStore.WaitReady();
    }

    internal static bool IsActive => Boundary.DataEvents.TileModifiers;
}
