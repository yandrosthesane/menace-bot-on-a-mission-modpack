using System.Collections.Generic;
using Il2CppMenace.Tactical;

namespace BOAM.DataEvents;

static class MovementBudgetEvent
{
    internal static void Enrich(Actor actor, Entity entity, Dictionary<string, object> payload)
    {
        if (!Boundary.DataEvents.MovementBudget) return;
        SyncTransforms.ComputeMovementBudget(actor, entity, payload);
    }
}
