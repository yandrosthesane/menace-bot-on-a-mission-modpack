using System.Collections.Generic;
using Il2CppMenace.Tactical.AI;
using Menace.SDK;

namespace BOAM.GameEvents;

static class OpponentTrackingEvent
{
    internal static (List<object> opponents, int count) Gather(AIFaction faction)
    {
        var oppList = new List<object>();
        if (!Boundary.GameEvents.OpponentTracking)
            return (oppList, 0);

        var opponents = faction.m_Opponents;
        int count = opponents?.Count ?? 0;
        if (opponents != null)
        {
            for (int i = 0; i < opponents.Count; i++)
            {
                try
                {
                    var opp = opponents[i];
                    var actorInfo = ActorRegistry.GetActorInfo(opp.Actor);
                    if (actorInfo == null) continue;
                    var (gameObj, _, entityId, _) = actorInfo.Value;
                    var (px, pz) = ActorRegistry.GetPos(gameObj);

                    oppList.Add(new
                    {
                        actor = ActorRegistry.GetUuid(entityId),
                        position = new { x = px, z = pz },
                        ttl = opp.TTL,
                        isKnown = opp.IsKnown(),
                        isAlive = opp.Actor.IsAlive()
                    });
                }
                catch { }
            }
        }
        return (oppList, count);
    }
}
