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
    private static string _pendingPlayerSkill;
    private static string _pendingPlayerActor;
    private static float _pendingSkillStartTime;

    /// Start tracking a player skill for duration measurement.
    /// Called from Patch_SelectSkill for all skills, and overridden by AttackTileStart for attacks.
    internal static void StartPlayerSkillTimer(string actor, string skillName)
    {
        _pendingPlayerSkill = skillName;
        _pendingPlayerActor = actor;
        _pendingSkillStartTime = UnityEngine.Time.time;
        // Block replay until AfterSkillUse fires
        SkillAnimationEndTime = float.MaxValue;
    }

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

            // Replay forcing: apply recorded damage to elements that weren't hit by the game
            if (BoamBridge.Instance?._replayActive == true && ReplayForcing.HasPendingMissedHits)
            {
                ReplayForcing.ApplyMissedElementHits();
            }

            // When a player skill finishes, amend the last log entry with the real duration
            // and release the replay gate
            if (_pendingPlayerSkill != null && name == _pendingPlayerSkill)
            {
                float actual = UnityEngine.Time.time - _pendingSkillStartTime;
                int actualMs = (int)(actual * 1000);
                SkillAnimationEndTime = UnityEngine.Time.time + 0.5f;
                BoamBridge.Logger.Msg($"[BOAM] DIAG PlayerSkillComplete: {name} (actual={actualMs}ms)");

                // Tell the engine to amend the last player action with the measured duration
                var payload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    actor = _pendingPlayerActor,
                    skill = name,
                    durationMs = actualMs
                });
                _pendingPlayerSkill = null;
                System.Threading.ThreadPool.QueueUserWorkItem(_ => EngineClient.Post("/hook/skill-complete", payload));
            }
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
            // Track player skill animations for the replay gate
            // For player attack skills: refine the start time (more accurate than useskill)
            if (info.HasValue && (info.Value.factionId == 1 || info.Value.factionId == 2))
            {
                _pendingSkillStartTime = UnityEngine.Time.time;
            }
            BoamBridge.Logger.Msg($"[BOAM] DIAG AttackStart: {uuid} {skillName} → ({tx},{tz}) duration={_attackDurationInSec}s");

            // Replay forcing: preload burst so ApplyMissedElementHits works even on total misses
            if (BoamBridge.Instance?._replayActive == true && ReplayForcing.HasElementHits && info.HasValue)
            {
                // Find target actor on the target tile
                try
                {
                    if (_targetTile != null && _targetTile.HasActor())
                    {
                        var targetActor = _targetTile.GetActor();
                        if (targetActor != null)
                        {
                            var targetInfo = ActorRegistry.GetActorInfo(targetActor);
                            if (targetInfo.HasValue)
                            {
                                var targetUuid = ActorRegistry.GetUuid(targetInfo.Value.entityId);
                                ReplayForcing.PreloadBurst(uuid, targetUuid);
                            }
                        }
                    }
                }
                catch { }
            }
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
