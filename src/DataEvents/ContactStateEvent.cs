using System.Collections.Generic;
using Menace.SDK;

namespace BOAM.DataEvents;

static class ContactStateEvent
{
    internal static void Enrich(GameObj gameObj, int vision, int factionId, Dictionary<string, object> payload)
    {
        if (!Boundary.DataEvents.ContactState) return;
        SyncTransforms.ComputeContactState(gameObj, vision, factionId, payload);
    }
}
