using System;
using Il2CppMenace.Tactical;
using Menace.SDK;

namespace BOAM;

/// <summary>
/// Sync transforms — enrich any hook payload with derived values from live game objects.
/// Reusable across any event that has an actor context (turn-end, tactical-ready, etc.).
/// All game object access is centralized here; the engine reads pre-computed values.
/// </summary>
internal static class SyncTransforms
{
    /// <summary>
    /// Compute whether this actor is in contact with any known opponent (within vision range).
    /// Injects "inContact" into the payload.
    /// </summary>
    internal static void ComputeContactState(GameObj gameObj, int vision, int factionId,
        System.Collections.Generic.Dictionary<string, object> payload)
    {
        try
        {
            var (actorX, actorZ) = ActorRegistry.GetPos(gameObj);
            bool inContact = false;

            var allActors = EntitySpawner.ListEntities(-1);
            if (allActors != null)
            {
                foreach (var a in allActors)
                {
                    var info = EntitySpawner.GetEntityInfo(a);
                    if (info == null || !info.IsAlive || info.FactionIndex == factionId) continue;
                    var pos = EntityMovement.GetPosition(a);
                    if (pos == null) continue;
                    int dx = (int)pos.Value.x - actorX;
                    int dz = (int)pos.Value.y - actorZ;
                    if (dx * dx + dz * dz <= vision * vision)
                    {
                        inContact = true;
                        break;
                    }
                }
            }

            payload["inContact"] = inContact;
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Warning($"[BOAM] ContactState transform failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Compute movement budget from live actor state: cheapest attack cost, cost per tile, max reachable distance.
    /// Injects "cheapestAttack", "costPerTile", "maxDist" into the payload.
    /// </summary>
    internal static void ComputeMovementBudget(Actor actor, Entity entity,
        System.Collections.Generic.Dictionary<string, object> payload)
    {
        try
        {
            int apStart = actor.GetActionPointsAtTurnStart();

            // Cheapest attack from live skills
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

            // Cost per tile from live movement type
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
            BoamBridge.Logger.Warning($"[BOAM] MovementBudget transform failed: {ex.Message}");
        }
    }
}
