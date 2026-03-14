using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using MelonLoader;
using Menace.SDK;

namespace BOAM;

/// <summary>
/// Executes action commands on the main thread.
/// Writes m_CurrentTile on TacticalState then calls HandleLeftClickOnTile.
/// </summary>
public static class BoamCommandExecutor
{
    /// Execute a single command. Must be called from the main thread.
    public static void Execute(BoamCommandServer.ActionCommand cmd, MelonLogger.Instance log)
    {
        try
        {
            switch (cmd.Action)
            {
                case "click":
                    ExecuteClick(cmd.X, cmd.Z, log);
                    break;
                case "useskill":
                    ExecuteUseSkill(cmd.Skill, cmd.X, cmd.Z, log);
                    break;
                case "endturn":
                    ExecuteEndTurn(log);
                    break;
                case "select":
                    ExecuteSelect(cmd.Actor, log);
                    break;
                default:
                    log.Warning($"[BOAM] Unknown command action: {cmd.Action}");
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error($"[BOAM] Command execution error ({cmd.Action}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// Write m_CurrentTile and call HandleLeftClickOnTile on the current action.
    private static void ExecuteClick(int x, int z, MelonLogger.Instance log)
    {
        // Build tile proxy
        var tileGameObj = TileMap.GetTile(x, z);
        if (tileGameObj.IsNull) { log.Warning($"[click] No tile at ({x}, {z})"); return; }
        var tileType = GameType.Find("Menace.Tactical.Tile")?.ManagedType;
        if (tileType == null) { log.Error("[click] Tile type not found"); return; }
        var tileProxy = tileType.GetConstructor(new[] { typeof(IntPtr) })?.Invoke(new object[] { tileGameObj.Pointer });

        // Get TacticalState
        var tsType = GameType.Find("Menace.States.TacticalState")?.ManagedType;
        var ts = tsType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Get" && m.GetParameters().Length == 0)?.Invoke(null, null);
        if (ts == null) { log.Error("[click] TacticalState null"); return; }

        // Write m_CurrentTile — this is what the game reads for click targeting
        var currentTileProp = ts.GetType().GetProperty("m_CurrentTile", BindingFlags.Public | BindingFlags.Instance);
        if (currentTileProp != null && currentTileProp.CanWrite)
            currentTileProp.SetValue(ts, tileProxy);
        else
        {
            var field = ts.GetType().GetField("m_CurrentTile", BindingFlags.Public | BindingFlags.Instance);
            if (field != null) field.SetValue(ts, tileProxy);
        }

        // Get current action and call HandleLeftClickOnTile
        var currentAction = ts.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetCurrentAction" && m.GetParameters().Length == 0)?.Invoke(ts, null);
        if (currentAction == null) { log.Warning("[click] No current action"); return; }

        var handleClick = currentAction.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "HandleLeftClickOnTile");
        if (handleClick == null) { log.Error("[click] HandleLeftClickOnTile not found"); return; }

        var activeActor = TacticalController.GetActiveActor();
        var actorType = GameType.Find("Menace.Tactical.Actor")?.ManagedType;
        object actorProxy = null;
        if (!activeActor.IsNull && actorType != null)
            actorProxy = Il2CppUtils.GetManagedProxy(activeActor, actorType);

        var paramCount = handleClick.GetParameters().Length;
        if (paramCount >= 2)
            handleClick.Invoke(currentAction, new[] { tileProxy, actorProxy });
        else
            handleClick.Invoke(currentAction, new[] { tileProxy });

        log.Msg($"[click] ({x}, {z})");
    }

    /// Activate a skill via TacticalState.TrySelectSkill.
    private static void ExecuteUseSkill(string skillName, int x, int z, MelonLogger.Instance log)
    {
        var actor = TacticalController.GetActiveActor();
        if (actor.IsNull) { log.Warning("[useskill] No actor selected"); return; }

        // Get managed actor proxy
        var actorType = GameType.Find("Menace.Tactical.Actor")?.ManagedType;
        if (actorType == null) { log.Error("[useskill] Actor type not found"); return; }
        var actorProxy = Il2CppUtils.GetManagedProxy(actor, actorType);
        if (actorProxy == null) { log.Error("[useskill] Failed to get actor proxy"); return; }

        // Get SkillContainer and find skill
        var getSkillsMethod = actorType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetSkills" && m.GetParameters().Length == 0);
        var skillContainer = getSkillsMethod?.Invoke(actorProxy, null);
        if (skillContainer == null) { log.Error("[useskill] No SkillContainer"); return; }

        // Find skill by iterating all skills
        object skill = null;
        var getAllSkillsMethod = skillContainer.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetAllSkills" && m.GetParameters().Length == 0);
        var skillsList = getAllSkillsMethod?.Invoke(skillContainer, null);
        if (skillsList != null)
        {
            var enumerator = skillsList.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetEnumerator" && m.GetParameters().Length == 0)
                ?.Invoke(skillsList, null);
            if (enumerator != null)
            {
                var moveNext = enumerator.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "MoveNext" && m.GetParameters().Length == 0);
                var current = enumerator.GetType().GetProperty("Current");
                while ((bool)moveNext.Invoke(enumerator, null))
                {
                    var s = current.GetValue(enumerator);
                    if (s == null) continue;
                    var id = Il2CppUtils.ToManagedString(
                        s.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "GetID" && m.GetParameters().Length == 0)
                        ?.Invoke(s, null));
                    var title = Il2CppUtils.ToManagedString(
                        s.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "GetTitle" && m.GetParameters().Length == 0)
                        ?.Invoke(s, null));
                    if (string.Equals(id, skillName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(title, skillName, StringComparison.OrdinalIgnoreCase))
                    {
                        skill = s;
                        break;
                    }
                }
            }
        }

        if (skill == null) { log.Warning($"[useskill] Skill '{skillName}' not found"); return; }

        // Cast BaseSkill to Skill
        var skillManagedType = GameType.Find("Menace.Tactical.Skills.Skill")?.ManagedType;
        if (skillManagedType != null)
        {
            var pointerProp = skill.GetType().GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
            if (pointerProp != null)
            {
                var ptr = (IntPtr)pointerProp.GetValue(skill);
                var skillPtrCtor = skillManagedType.GetConstructor(new[] { typeof(IntPtr) });
                if (skillPtrCtor != null)
                    skill = skillPtrCtor.Invoke(new object[] { ptr });
            }
        }

        // TrySelectSkill
        var tsType = GameType.Find("Menace.States.TacticalState")?.ManagedType;
        var ts = tsType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Get" && m.GetParameters().Length == 0)?.Invoke(null, null);
        if (ts == null) { log.Error("[useskill] TacticalState null"); return; }

        var trySelect = ts.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "TrySelectSkill" && m.GetParameters().Length == 1);
        if (trySelect == null) { log.Error("[useskill] TrySelectSkill not found"); return; }

        trySelect.Invoke(ts, new[] { skill });
        log.Msg($"[useskill] '{skillName}' on ({x}, {z})");
    }

    /// End the current actor's turn.
    private static void ExecuteEndTurn(MelonLogger.Instance log)
    {
        var tsType = GameType.Find("Menace.States.TacticalState")?.ManagedType;
        var ts = tsType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Get" && m.GetParameters().Length == 0)?.Invoke(null, null);
        if (ts == null) { log.Error("[endturn] TacticalState null"); return; }

        var endTurn = ts.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "EndTurn" && m.GetParameters().Length == 0);
        if (endTurn == null) { log.Error("[endturn] EndTurn not found"); return; }

        endTurn.Invoke(ts, null);
        log.Msg("[endturn] done");
    }

    /// Select an actor by stable UUID — resolves to entity ID via BoamBridge registry.
    private static void ExecuteSelect(string actor, MelonLogger.Instance log)
    {
        if (string.IsNullOrEmpty(actor)) { log.Warning("[select] No actor specified"); return; }

        var entityId = BoamBridge.GetEntityId(actor);
        if (entityId < 0) { log.Warning($"[select] Unknown actor: {actor}"); return; }

        var allEntities = EntitySpawner.ListEntities();
        GameObj found = GameObj.Null;
        foreach (var e in allEntities)
        {
            var info = EntitySpawner.GetEntityInfo(e);
            if (info != null && info.EntityId == entityId) { found = e; break; }
        }
        if (found.IsNull) { log.Warning($"[select] Entity not found for {actor} (id={entityId})"); return; }

        var ok = TacticalController.SetActiveActor(found);
        log.Msg(ok ? $"[select] Selected {actor}" : $"[select] Failed {actor}");
    }
}
