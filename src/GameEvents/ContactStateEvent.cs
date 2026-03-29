using System;
using System.Collections.Generic;
using Il2CppMenace.Tactical;
using Menace.SDK;

namespace BOAM.GameEvents;

static class ContactStateEvent
{
    internal static bool IsActive => Boundary.GameEvents.ContactState;

    internal static void Enrich(GameObj gameObj, int vision, int factionId, Dictionary<string, object> payload)
    {
        if (!IsActive) return;
        try
        {
            var (actorX, actorZ) = ActorRegistry.GetPos(gameObj);
            bool inRange = false;
            bool detected = false;

            var allActors = EntitySpawner.ListEntities(-1);
            if (allActors != null)
            {
                foreach (var a in allActors)
                {
                    var info = EntitySpawner.GetEntityInfo(a);
                    if (info == null || !info.IsAlive || info.FactionIndex == factionId) continue;

                    if (!inRange)
                    {
                        var pos = EntityMovement.GetPosition(a);
                        if (pos != null)
                        {
                            int dx = (int)pos.Value.x - actorX;
                            int dz = (int)pos.Value.y - actorZ;
                            if (dx * dx + dz * dz <= vision * vision)
                                inRange = true;
                        }
                    }

                    if (!detected)
                    {
                        var opponentActor = new Actor(a.Pointer);
                        if (opponentActor.IsDetectedByFaction(factionId))
                            detected = true;
                    }

                    if (inRange && detected) break;
                }
            }

            payload["inRange"] = inRange;
            payload["inContact"] = detected;
        }
        catch (Exception ex)
        {
            BoamBridge.Logger?.Warning($"[BOAM] ContactState error: {ex.Message}");
        }
    }
}
