using System;
using System.Collections.Generic;
using MelonLoader;

namespace BOAM.GameEvents;

static class ObjectiveDetectionEvent
{
    internal static HashSet<IntPtr> GetTargets(MelonLogger.Instance log)
    {
        if (!Boundary.GameEvents.ObjectiveDetection)
            return new HashSet<IntPtr>();

        var targets = new HashSet<IntPtr>();
        try
        {
            var tm = Il2CppMenace.Tactical.TacticalManager.Get();
            if (tm == null) return targets;
            var mission = tm.GetMission();
            if (mission == null) return targets;
            var om = mission.Objectives;
            if (om == null) return targets;
            var objs = om.m_Objectives;
            if (objs == null) return targets;
            for (int i = 0; i < objs.Length; i++)
            {
                try
                {
                    var obj = objs[i];
                    if (obj == null || obj.GetState() != Il2CppMenace.Tactical.Objectives.ObjectiveState.Ongoing)
                        continue;
                    var target = obj.GetTarget();
                    if (target != null)
                    {
                        targets.Add(target.Pointer);
                        log.Msg($"[BOAM] Objective target: {target.Pointer}");
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[BOAM] ObjectiveDetection error: {ex.Message}");
        }
        log.Msg($"[BOAM] Found {targets.Count} objective targets");
        return targets;
    }
}
