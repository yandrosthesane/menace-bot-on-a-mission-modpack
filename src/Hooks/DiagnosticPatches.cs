using Il2CppMenace.Tactical;

namespace BOAM;

/// <summary>
/// Harmony postfix targets for skill lifecycle hooks.
/// Logic delegated to ActionLoggingEvent.
/// </summary>
static class Patch_Diagnostics
{
    internal static float SkillAnimationEndTime;

    public static void OnAfterSkillUse(Il2CppMenace.Tactical.Skills.Skill _skill)
    {
        try
        {
            var name = _skill?.GetTitle() ?? "null";
            DataEvents.ActionLoggingEvent.OnAfterSkillUse(name);
        }
        catch { }
    }

    public static void OnAttackTileStart(Actor _actor, Il2CppMenace.Tactical.Skills.Skill _skill, Tile _targetTile, float _attackDurationInSec)
    {
        try
        {
            var skillName = _skill?.GetTitle() ?? "null";
            int tx = _targetTile?.GetX() ?? 0;
            int tz = _targetTile?.GetZ() ?? 0;
            DataEvents.ActionLoggingEvent.OnAttackTileStart(_actor, skillName, tx, tz, _attackDurationInSec);
        }
        catch { }
    }

    public static void OnActionPointsChanged(Actor _actor, int _oldAP, int _newAP)
    {
        try
        {
            DataEvents.ActionLoggingEvent.OnActionPointsChanged(_actor, _oldAP, _newAP);
        }
        catch { }
    }
}
