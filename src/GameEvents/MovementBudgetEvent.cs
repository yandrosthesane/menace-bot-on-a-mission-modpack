using System;
using System.Collections.Generic;
using Il2CppMenace.Tactical;

namespace BOAM.GameEvents;

static class MovementBudgetEvent
{
    internal static bool IsActive => Boundary.GameEvents.MovementBudget;

    internal static void Enrich(Actor actor, Entity entity, Dictionary<string, object> payload)
    {
        if (!IsActive) return;
        try
        {
            int cheapestAttack = 0;
            var attacks = actor.GetAllAttacks();
            if (attacks != null)
            {
                int minCost = int.MaxValue;
                for (int i = 0; i < attacks.Count; i++)
                {
                    var skill = attacks[i];
                    if (skill == null) continue;
                    int cost = skill.GetActionPointCost();
                    if (cost > 0 && cost < minCost) minCost = cost;
                }
                if (minCost < int.MaxValue) cheapestAttack = minCost;
            }

            int costPerTile = 16;
            var movType = entity.GetTemplate()?.MovementType;
            if (movType != null)
            {
                int lowest = movType.GetLowestMovementCost();
                if (lowest > 0) costPerTile = lowest;
            }

            payload["cheapestAttack"] = cheapestAttack;
            payload["costPerTile"] = costPerTile;
        }
        catch (Exception ex)
        {
            BoamBridge.Logger?.Warning($"[BOAM] MovementBudget error: {ex.Message}");
        }
    }
}
