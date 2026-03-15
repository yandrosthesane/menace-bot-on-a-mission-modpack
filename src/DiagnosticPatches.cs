using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using HarmonyLib;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;
using Il2CppMenace.Tactical.AI.Behaviors;
using Il2CppMenace.Tactical.Skills;
using MelonLoader;
using Menace.SDK;

namespace BOAM;

/// <summary>
/// Diagnostic patches for tracing turn/skill lifecycle during replay.
/// </summary>
static class Patch_Diagnostics
{
    internal static float SkillAnimationEndTime;

    public static void OnTurnEnd(Actor _actor)
    {
        try
        {
            var info = ActorRegistry.GetActorInfo(_actor);
            var uuid = info.HasValue ? ActorRegistry.GetUuid(info.Value.entityId) : "null";
            BoamBridge.Logger.Msg($"[BOAM] DIAG TurnEnd: {uuid}");
        }
        catch { }
    }

    public static void OnAfterSkillUse(Il2CppMenace.Tactical.Skills.Skill _skill)
    {
        try
        {
            var name = _skill?.GetTitle() ?? "null";
            BoamBridge.Logger.Msg($"[BOAM] DIAG AfterSkillUse: {name}");
        }
        catch { }
    }

    public static void OnAttackTileStart(Actor _actor, Il2CppMenace.Tactical.Skills.Skill _skill, Il2CppMenace.Tactical.Tile _targetTile, float _attackDurationInSec)
    {
        try
        {
            SkillAnimationEndTime = UnityEngine.Time.time + _attackDurationInSec + 0.5f;
            var info = ActorRegistry.GetActorInfo(_actor);
            var uuid = info.HasValue ? ActorRegistry.GetUuid(info.Value.entityId) : "null";
            var skillName = _skill?.GetTitle() ?? "null";
            int tx = _targetTile?.GetX() ?? 0;
            int tz = _targetTile?.GetZ() ?? 0;
            BoamBridge.Logger.Msg($"[BOAM] DIAG AttackStart: {uuid} {skillName} → ({tx},{tz}) duration={_attackDurationInSec}s");
        }
        catch { }
    }

    public static void OnActionPointsChanged(Actor _actor, int _oldAP, int _newAP)
    {
        try
        {
            var info = ActorRegistry.GetActorInfo(_actor);
            var uuid = info.HasValue ? ActorRegistry.GetUuid(info.Value.entityId) : "null";
            BoamBridge.Logger.Msg($"[BOAM] DIAG AP: {uuid} {_oldAP} → {_newAP}");
        }
        catch { }
    }
}
