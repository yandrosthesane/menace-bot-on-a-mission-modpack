using System;
using System.Linq;
using System.Reflection;
using MelonLoader;
using Menace.SDK;

namespace BOAM;

/// <summary>
/// Executes action commands on the main thread.
/// </summary>
public static class BoamCommandExecutor
{
    // Cached reflection lookups — resolved once, reused across invocations
    private static Type _tsType;
    private static MethodInfo _tsGet;
    private static MethodInfo _tsEndTurn;
    private static MethodInfo _tsTrySelectSkill;
    private static PropertyInfo _tsCurrentTile;
    private static MethodInfo _tsGetCurrentAction;
    private static Type _tileType;
    private static ConstructorInfo _tilePtrCtor;
    private static Type _actorType;
    private static Type _skillManagedType;
    private static ConstructorInfo _skillPtrCtor;
    private static bool _cacheInitialized;

    private static bool EnsureCache(MelonLogger.Instance log)
    {
        if (_cacheInitialized) return _tsType != null;

        _tsType = GameType.Find("Menace.States.TacticalState")?.ManagedType;
        if (_tsType == null) { log.Error("[BOAM] TacticalState type not found"); return false; }

        _tsGet = _tsType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Get" && m.GetParameters().Length == 0);
        _tsEndTurn = _tsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "EndTurn" && m.GetParameters().Length == 0);
        _tsTrySelectSkill = _tsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "TrySelectSkill" && m.GetParameters().Length == 1);

        _tileType = GameType.Find("Menace.Tactical.Tile")?.ManagedType;
        _tilePtrCtor = _tileType?.GetConstructor(new[] { typeof(IntPtr) });
        _actorType = GameType.Find("Menace.Tactical.Actor")?.ManagedType;
        _skillManagedType = GameType.Find("Menace.Tactical.Skills.Skill")?.ManagedType;
        _skillPtrCtor = _skillManagedType?.GetConstructor(new[] { typeof(IntPtr) });

        _cacheInitialized = true;
        log.Msg("[BOAM] CommandExecutor reflection cache initialized");
        return true;
    }

    private static object GetTacticalState()
    {
        return _tsGet?.Invoke(null, null);
    }

    private static object GetTileProxy(int x, int z)
    {
        var tileGameObj = TileMap.GetTile(x, z);
        if (tileGameObj.IsNull || _tilePtrCtor == null) return null;
        return _tilePtrCtor.Invoke(new object[] { tileGameObj.Pointer });
    }

    /// Execute a single command. Must be called from the main thread.
    public static void Execute(BridgeServer.ActionCommand cmd, MelonLogger.Instance log)
    {
        try
        {
            if (!EnsureCache(log)) return;

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

    private static void ExecuteClick(int x, int z, MelonLogger.Instance log)
    {
        var tileProxy = GetTileProxy(x, z);
        if (tileProxy == null) { log.Warning($"[click] No tile at ({x}, {z})"); return; }

        var ts = GetTacticalState();
        if (ts == null) { log.Error("[click] TacticalState null"); return; }

        // Write m_CurrentTile — the game reads this for click targeting
        _tsCurrentTile ??= ts.GetType().GetProperty("m_CurrentTile", BindingFlags.Public | BindingFlags.Instance);
        if (_tsCurrentTile != null && _tsCurrentTile.CanWrite)
            _tsCurrentTile.SetValue(ts, tileProxy);
        else
        {
            var field = ts.GetType().GetField("m_CurrentTile", BindingFlags.Public | BindingFlags.Instance);
            field?.SetValue(ts, tileProxy);
        }

        // Get current action and call HandleLeftClickOnTile
        _tsGetCurrentAction ??= ts.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetCurrentAction" && m.GetParameters().Length == 0);
        var currentAction = _tsGetCurrentAction?.Invoke(ts, null);
        if (currentAction == null) { log.Warning("[click] No current action"); return; }

        var handleClick = currentAction.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "HandleLeftClickOnTile");
        if (handleClick == null) { log.Error("[click] HandleLeftClickOnTile not found"); return; }

        var activeActor = TacticalController.GetActiveActor();
        object actorProxy = null;
        if (!activeActor.IsNull && _actorType != null)
            actorProxy = Il2CppUtils.GetManagedProxy(activeActor, _actorType);

        if (handleClick.GetParameters().Length >= 2)
            handleClick.Invoke(currentAction, new[] { tileProxy, actorProxy });
        else
            handleClick.Invoke(currentAction, new[] { tileProxy });

        log.Msg($"[click] ({x}, {z})");
    }

    private static void ExecuteUseSkill(string skillName, int x, int z, MelonLogger.Instance log)
    {
        var actor = TacticalController.GetActiveActor();
        if (actor.IsNull) { log.Warning("[useskill] No actor selected"); return; }

        var actorProxy = Il2CppUtils.GetManagedProxy(actor, _actorType);
        if (actorProxy == null) { log.Error("[useskill] Failed to get actor proxy"); return; }

        // Find skill by iterating all skills
        var getSkills = _actorType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetSkills" && m.GetParameters().Length == 0);
        var skillContainer = getSkills?.Invoke(actorProxy, null);
        if (skillContainer == null) { log.Error("[useskill] No SkillContainer"); return; }

        var getAllSkills = skillContainer.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetAllSkills" && m.GetParameters().Length == 0);
        var skillsList = getAllSkills?.Invoke(skillContainer, null);
        if (skillsList == null) { log.Error("[useskill] No skills list"); return; }

        object skill = null;
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

        if (skill == null) { log.Warning($"[useskill] Skill '{skillName}' not found"); return; }

        // Cast BaseSkill to Skill via pointer constructor
        if (_skillPtrCtor != null)
        {
            var pointerProp = skill.GetType().GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
            if (pointerProp != null)
                skill = _skillPtrCtor.Invoke(new object[] { (IntPtr)pointerProp.GetValue(skill) });
        }

        var ts = GetTacticalState();
        if (ts == null) { log.Error("[useskill] TacticalState null"); return; }

        _tsTrySelectSkill.Invoke(ts, new[] { skill });
        log.Msg($"[useskill] '{skillName}' on ({x}, {z})");
    }

    private static void ExecuteEndTurn(MelonLogger.Instance log)
    {
        var ts = GetTacticalState();
        if (ts == null) { log.Error("[endturn] TacticalState null"); return; }

        _tsEndTurn.Invoke(ts, null);
        log.Msg("[endturn] done");
    }

    // Cached reflection for direct slot selection (portrait click equivalent)
    private static MethodInfo _tsGetUI;
    private static MethodInfo _uiGetTurnBar;
    private static MethodInfo _turnBarGetSlot;   // GetSlot(Actor) -> UnitsTurnBarSlot
    private static MethodInfo _slotOnLeftClicked;

    private static void ExecuteSelect(string actor, MelonLogger.Instance log)
    {
        if (string.IsNullOrEmpty(actor)) { log.Warning("[select] No actor specified"); return; }

        var entityId = ActorRegistry.GetEntityId(actor);
        if (entityId < 0) { log.Warning($"[select] Unknown actor: {actor} — not in ActorRegistry"); return; }

        log.Msg($"[select] BEGIN target={actor} entityId={entityId}");

        // Check if already the active actor
        var activeGameObj = TacticalController.GetActiveActor();
        if (!activeGameObj.IsNull)
        {
            var activeInfo = EntitySpawner.GetEntityInfo(activeGameObj);
            if (activeInfo != null && activeInfo.EntityId == entityId)
            {
                log.Msg($"[select] {actor} already active — no-op");
                return;
            }
        }

        // Get UnitsTurnBar via TacticalState → GetUI() → GetUnitsTurnBar()
        var ts = GetTacticalState();
        if (ts == null) { log.Error("[select] TacticalState null"); return; }

        _tsGetUI ??= ts.GetType().GetMethod("GetUI", BindingFlags.Public | BindingFlags.Instance);
        var ui = _tsGetUI?.Invoke(ts, null);
        if (ui == null) { log.Error("[select] UITactical null"); return; }

        _uiGetTurnBar ??= ui.GetType().GetMethod("GetUnitsTurnBar", BindingFlags.Public | BindingFlags.Instance);
        var turnBar = _uiGetTurnBar?.Invoke(ui, null);
        if (turnBar == null) { log.Error("[select] UnitsTurnBar null"); return; }

        // Find target actor proxy by scanning entities
        object targetActorProxy = null;
        var allActors = EntitySpawner.ListEntities(-1);
        if (allActors != null)
        {
            foreach (var a in allActors)
            {
                var info = EntitySpawner.GetEntityInfo(a);
                if (info != null && info.EntityId == entityId)
                {
                    targetActorProxy = Il2CppUtils.GetManagedProxy(a, _actorType);
                    break;
                }
            }
        }
        if (targetActorProxy == null) { log.Error($"[select] No actor found for entityId={entityId}"); return; }

        // Get slot via GetSlot(Actor) and invoke OnLeftClicked (portrait click)
        var turnBarType = turnBar.GetType();
        _turnBarGetSlot ??= turnBarType.GetMethod("GetSlot", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (_turnBarGetSlot == null) { log.Error("[select] GetSlot method not found"); return; }

        var targetSlot = _turnBarGetSlot.Invoke(turnBar, new[] { targetActorProxy });
        if (targetSlot == null) { log.Warning($"[select] GetSlot returned null for {actor}"); return; }

        _slotOnLeftClicked ??= targetSlot.GetType().GetMethod("OnLeftClicked",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        if (_slotOnLeftClicked == null) { log.Error("[select] OnLeftClicked not found on slot"); return; }

        _slotOnLeftClicked.Invoke(targetSlot, new object[] { targetSlot });

        activeGameObj = TacticalController.GetActiveActor();
        if (!activeGameObj.IsNull)
        {
            var resultInfo = EntitySpawner.GetEntityInfo(activeGameObj);
            if (resultInfo != null && resultInfo.EntityId == entityId)
            {
                log.Msg($"[select] OK: {actor} selected");
                return;
            }
            log.Warning($"[select] OnLeftClicked invoked but active actor is {ActorRegistry.GetUuid(resultInfo?.EntityId ?? 0)}");
        }

        log.Warning($"[select] FAILED to select {actor} (entityId={entityId})");
    }
}
