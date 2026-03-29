using System.Text.Json;
using System.Threading;
using Il2CppMenace.Tactical;

namespace BOAM.DataEvents;

static class ActionLoggingEvent
{
    internal static bool IsActive => Boundary.DataEvents.ActionLogging;

    private static string _pendingPlayerSkill;
    private static string _pendingPlayerActor;
    private static float _pendingSkillStartTime;

    internal static void StartPlayerSkillTimer(string actor, string skillName)
    {
        if (!IsActive) return;
        _pendingPlayerSkill = skillName;
        _pendingPlayerActor = actor;
        _pendingSkillStartTime = UnityEngine.Time.time;
        Patch_Diagnostics.SkillAnimationEndTime = float.MaxValue;
    }

    internal static void OnAfterSkillUse(string skillName)
    {
        if (!IsActive) return;
        BoamBridge.Logger.Msg($"[BOAM] AfterSkillUse: {skillName}");

        if (_pendingPlayerSkill != null && skillName == _pendingPlayerSkill)
        {
            float actual = UnityEngine.Time.time - _pendingSkillStartTime;
            int actualMs = (int)(actual * 1000);
            Patch_Diagnostics.SkillAnimationEndTime = UnityEngine.Time.time + 0.5f;
            BoamBridge.Logger.Msg($"[BOAM] PlayerSkillComplete: {skillName} ({actualMs}ms)");

            var payload = JsonSerializer.Serialize(new
            {
                actor = _pendingPlayerActor,
                skill = skillName,
                durationMs = actualMs
            });
            _pendingPlayerSkill = null;
            ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("skill-complete", payload));
        }
    }

    internal static void OnAttackTileStart(Actor actor, string skillName, int tileX, int tileZ, float duration)
    {
        Patch_Diagnostics.SkillAnimationEndTime = UnityEngine.Time.time + duration + 0.5f;

        var info = ActorRegistry.GetActorInfo(actor);
        if (info.HasValue && (info.Value.factionId == 1 || info.Value.factionId == 2))
            _pendingSkillStartTime = UnityEngine.Time.time;

        if (!IsActive) return;
        var uuid = info.HasValue ? ActorRegistry.GetUuid(info.Value.entityId) : "?";
        BoamBridge.Logger.Msg($"[BOAM] AttackStart: {uuid} {skillName} → ({tileX},{tileZ}) duration={duration}s");
    }

    internal static void OnActionPointsChanged(Actor actor, int oldAP, int newAP)
    {
        if (!IsActive) return;
        var info = ActorRegistry.GetActorInfo(actor);
        var uuid = info.HasValue ? ActorRegistry.GetUuid(info.Value.entityId) : "?";
        BoamBridge.Logger.Msg($"[BOAM] AP: {uuid} {oldAP} → {newAP}");
    }
}
