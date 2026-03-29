using System;
using Il2CppMenace.Tactical;
using Menace.SDK;

namespace BOAM.GameEvents;

static class MinimapUnitsEvent
{
    internal static bool IsActive => Boundary.GameEvents.MinimapUnits;

    internal static void UpdatePosition(string actorUuid, int x, int z)
    {
        if (!IsActive) return;
        TacticalMap.TacticalMapState.UpdateUnitPosition(actorUuid, x, z);
    }

    internal static void SetActiveActor(string actorUuid, int x, int z)
    {
        if (!IsActive) return;
        TacticalMap.TacticalMapState.ActiveActor = actorUuid;
        TacticalMap.TacticalMapState.UpdateUnitPosition(actorUuid, x, z);
    }

    internal static void PopulateInitial(int round)
    {
        PopulateOverlay(0, round);
        if (IsActive)
            BoamBridge.Logger?.Msg($"[BOAM] TacticalMap — Initial population: {TacticalMap.TacticalMapState.GetUnitsSnapshot().Count} units");
    }

    internal static void PopulateOverlay(int factionId, int round)
    {
        if (!IsActive) return;

        var overlayUnits = new System.Collections.Generic.List<TacticalMap.OverlayUnit>();
        try
        {
            var allActors = EntitySpawner.ListEntities(-1);
            if (allActors != null)
            {
                foreach (var a in allActors)
                {
                    var aInfo = EntitySpawner.GetEntityInfo(a);
                    if (aInfo == null || !aInfo.IsAlive) continue;
                    var aPos = EntityMovement.GetPosition(a);
                    if (aPos == null) continue;

                    int visibility = LineOfSight.GetVisibilityState(a);
                    bool isPlayerSide = aInfo.FactionIndex == 1 || aInfo.FactionIndex == 2 || aInfo.FactionIndex == 4;
                    bool knownToPlayer = isPlayerSide || visibility == 1 || visibility == 3;

                    var aGo = new GameObj(a.Pointer);
                    var aTpl = aGo.ReadObj("m_Template");
                    var templateName = aTpl.IsNull ? "" : (aTpl.GetName() ?? "");

                    var leaderName = "";
                    try
                    {
                        var unitActor = new UnitActor(a.Pointer);
                        var leader = unitActor.GetLeader();
                        if (leader != null)
                        {
                            var nn = leader.GetNickname();
                            if (nn != null) leaderName = nn.GetTranslated() ?? "";
                        }
                    }
                    catch { }

                    var actorUuid = ActorRegistry.GetUuid(aInfo.EntityId);

                    overlayUnits.Add(new TacticalMap.OverlayUnit
                    {
                        Actor = actorUuid,
                        Label = actorUuid,
                        FactionIndex = aInfo.FactionIndex,
                        X = aPos.Value.x,
                        Y = aPos.Value.y,
                        KnownToPlayer = knownToPlayer,
                        Template = templateName,
                        Leader = leaderName
                    });
                }
            }
        }
        catch { }
        TacticalMap.TacticalMapState.SetUnits(overlayUnits);
        TacticalMap.TacticalMapState.CurrentRound = round;
        TacticalMap.TacticalMapState.CurrentFaction = factionId;
    }
}
