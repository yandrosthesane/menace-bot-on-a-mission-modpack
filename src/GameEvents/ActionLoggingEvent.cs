using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Il2CppMenace.Tactical;
using MelonLoader;
using Menace.SDK;

namespace BOAM.GameEvents;

static class ActionLoggingEvent
{
    internal static bool IsActive => Boundary.GameEvents.ActionLogging;

    private static string _pendingPlayerSkill;
    private static string _pendingPlayerActor;
    private static float _pendingSkillStartTime;

    // Guard for duplicate EndTurn calls
    private static int _lastActorId = -1;
    private static int _lastRound = -1;

    private static bool IsAiFaction(int factionId) =>
        factionId != (int)Menace.SDK.FactionType.Player && factionId != (int)Menace.SDK.FactionType.PlayerAI;

    internal static void Register(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
    {
        var tmType = typeof(TacticalManager);

        // AI move
        try
        {
            var m = Array.Find(tmType.GetMethods(), m => m.Name == "InvokeOnMovementFinished" && m.GetParameters().Length == 2);
            if (m != null) { harmony.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(ActionLoggingEvent), nameof(OnAiMovementFinished))); log.Msg("[BOAM] Patched InvokeOnMovementFinished (action logging)"); }
        }
        catch (Exception ex) { log.Error($"[BOAM] Failed to patch AI movement: {ex.Message}"); }

        // AI skill
        try
        {
            var m = Array.Find(tmType.GetMethods(), m => m.Name == "InvokeOnSkillUse" && m.GetParameters().Length == 3);
            if (m != null) { harmony.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(ActionLoggingEvent), nameof(OnAiSkillUse))); log.Msg("[BOAM] Patched InvokeOnSkillUse (action logging)"); }
        }
        catch (Exception ex) { log.Error($"[BOAM] Failed to patch AI skill: {ex.Message}"); }

        // DIAG: AfterSkillUse, AttackTileStart, ActionPointsChanged
        try
        {
            var m = Array.Find(tmType.GetMethods(), m => m.Name == "InvokeOnAfterSkillUse" && m.GetParameters().Length == 1);
            if (m != null) { harmony.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(ActionLoggingEvent), nameof(OnAfterSkillUseHook))); log.Msg("[BOAM] Patched InvokeOnAfterSkillUse"); }

            m = Array.Find(tmType.GetMethods(), m => m.Name == "InvokeOnAttackTileStart" && m.GetParameters().Length == 4);
            if (m != null) { harmony.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(ActionLoggingEvent), nameof(OnAttackTileStartHook))); log.Msg("[BOAM] Patched InvokeOnAttackTileStart"); }

            m = Array.Find(tmType.GetMethods(), m => m.Name == "InvokeOnActionPointsChanged" && m.GetParameters().Length == 3);
            if (m != null) { harmony.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(ActionLoggingEvent), nameof(OnActionPointsChangedHook))); log.Msg("[BOAM] Patched InvokeOnActionPointsChanged"); }
        }
        catch (Exception ex) { log.Error($"[BOAM] DIAG patches failed: {ex.Message}"); }

        // Player EndTurn
        try
        {
            var tsType = GameType.Find("Menace.States.TacticalState")?.ManagedType;
            if (tsType != null)
            {
                var m = tsType.GetMethod("EndTurn", BindingFlags.Public | BindingFlags.Instance);
                if (m != null) { harmony.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(ActionLoggingEvent), nameof(OnPlayerEndTurn))); log.Msg("[BOAM] Patched TacticalState.EndTurn"); }
            }
        }
        catch (Exception ex) { log.Error($"[BOAM] Failed to patch EndTurn: {ex.Message}"); }

        // Player click (5 action classes)
        try
        {
            var clickPostfix = new HarmonyLib.HarmonyMethod(typeof(ActionLoggingEvent), nameof(OnClickOnTile));
            var actionTypes = new[] {
                typeof(Il2CppMenace.States.NoneAction),
                typeof(Il2CppMenace.States.ComputePathAction),
                typeof(Il2CppMenace.States.SkillAction),
                typeof(Il2CppMenace.States.SelectAoETilesAction),
                typeof(Il2CppMenace.States.OffmapAbilityAction),
            };
            foreach (var actionType in actionTypes)
            {
                try
                {
                    var m = actionType.GetMethod("HandleLeftClickOnTile");
                    if (m != null) { harmony.Patch(m, postfix: clickPostfix); log.Msg($"[BOAM] Patched {actionType.Name}.HandleLeftClickOnTile"); }
                }
                catch (Exception ex) { log.Warning($"[BOAM] {actionType.Name} patch failed: {ex.Message}"); }
            }
        }
        catch (Exception ex) { log.Error($"[BOAM] Failed to patch click logging: {ex.Message}"); }

        // Player skill select
        try
        {
            var tsType = GameType.Find("Menace.States.TacticalState")?.ManagedType;
            if (tsType != null)
            {
                var m = tsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "TrySelectSkill" && m.GetParameters().Length == 1);
                if (m != null) { harmony.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(ActionLoggingEvent), nameof(OnSelectSkill))); log.Msg("[BOAM] Patched TacticalState.TrySelectSkill"); }
            }
        }
        catch (Exception ex) { log.Error($"[BOAM] Failed to patch TrySelectSkill: {ex.Message}"); }
    }

    // --- Helpers called from other events ---

    internal static float SkillAnimationEndTime;

    internal static void StartPlayerSkillTimer(string actor, string skillName)
    {
        if (!IsActive) return;
        _pendingPlayerSkill = skillName;
        _pendingPlayerActor = actor;
        _pendingSkillStartTime = UnityEngine.Time.time;
        SkillAnimationEndTime = float.MaxValue;
    }

    internal static void LogAiEndTurn(int round, int factionId, string actorUuid, int tileX, int tileZ)
    {
        if (!IsActive || !IsAiFaction(factionId)) return;
        var payload = JsonSerializer.Serialize(new
        {
            hook = "ai-action", round, faction = factionId, actor = actorUuid,
            actionType = "ai_endturn", skillName = "", tile = new { x = tileX, z = tileZ }
        });
        ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("ai-action", payload));
    }

    // --- Harmony targets ---

    public static void OnAiMovementFinished(Actor _actor, Tile _to)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsEngineReady || !IsActive) return;
            if (_actor == null || _to == null) return;

            var actorInfo = ActorRegistry.GetActorInfo(_actor);
            if (actorInfo == null) return;
            var (_, factionId, entityId, _) = actorInfo.Value;
            if (!IsAiFaction(factionId)) return;

            var actorUuid = ActorRegistry.GetUuid(entityId);
            int tileX = _to.GetX(), tileZ = _to.GetZ();

            var payload = JsonSerializer.Serialize(new
            {
                hook = "ai-action", round = bridge.Round, faction = factionId, actor = actorUuid,
                actionType = "ai_move", skillName = "", tile = new { x = tileX, z = tileZ }
            });
            BoamBridge.Logger.Msg($"[BOAM] ai-action {actorUuid}: ai_move ({tileX},{tileZ})");
            ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("ai-action", payload));
        }
        catch (Exception ex) { BoamBridge.Logger.Error($"[BOAM] ai-action move error: {ex.Message}"); }
    }

    public static void OnAiSkillUse(Actor _actor, Il2CppMenace.Tactical.Skills.Skill _skill, Tile _targetTile)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsEngineReady || !IsActive) return;
            if (_actor == null) return;

            var actorInfo = ActorRegistry.GetActorInfo(_actor);
            if (actorInfo == null) return;
            var (_, factionId, entityId, _) = actorInfo.Value;
            if (!IsAiFaction(factionId)) return;

            var actorUuid = ActorRegistry.GetUuid(entityId);
            var skillName = "";
            try { skillName = _skill?.GetTitle() ?? ""; } catch { }
            int tileX = 0, tileZ = 0;
            try { if (_targetTile != null) { tileX = _targetTile.GetX(); tileZ = _targetTile.GetZ(); } } catch { }

            var payload = JsonSerializer.Serialize(new
            {
                hook = "ai-action", round = bridge.Round, faction = factionId, actor = actorUuid,
                actionType = "ai_useskill", skillName, tile = new { x = tileX, z = tileZ }
            });
            BoamBridge.Logger.Msg($"[BOAM] ai-action {actorUuid}: ai_useskill {skillName} ({tileX},{tileZ})");
            ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("ai-action", payload));
        }
        catch (Exception ex) { BoamBridge.Logger.Error($"[BOAM] ai-action useskill error: {ex.Message}"); }
    }

    public static void OnAfterSkillUseHook(Il2CppMenace.Tactical.Skills.Skill _skill)
    {
        try
        {
            var name = _skill?.GetTitle() ?? "null";
            if (!IsActive) return;
            BoamBridge.Logger.Msg($"[BOAM] AfterSkillUse: {name}");

            if (_pendingPlayerSkill != null && name == _pendingPlayerSkill)
            {
                float actual = UnityEngine.Time.time - _pendingSkillStartTime;
                int actualMs = (int)(actual * 1000);
                SkillAnimationEndTime = UnityEngine.Time.time + 0.5f;
                BoamBridge.Logger.Msg($"[BOAM] PlayerSkillComplete: {name} ({actualMs}ms)");

                var payload = JsonSerializer.Serialize(new { actor = _pendingPlayerActor, skill = name, durationMs = actualMs });
                _pendingPlayerSkill = null;
                ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("skill-complete", payload));
            }
        }
        catch { }
    }

    public static void OnAttackTileStartHook(Actor _actor, Il2CppMenace.Tactical.Skills.Skill _skill, Tile _targetTile, float _attackDurationInSec)
    {
        try
        {
            SkillAnimationEndTime = UnityEngine.Time.time + _attackDurationInSec + 0.5f;
            var info = ActorRegistry.GetActorInfo(_actor);
            if (info.HasValue && (info.Value.factionId == 1 || info.Value.factionId == 2))
                _pendingSkillStartTime = UnityEngine.Time.time;

            if (!IsActive) return;
            var uuid = info.HasValue ? ActorRegistry.GetUuid(info.Value.entityId) : "?";
            var skillName = _skill?.GetTitle() ?? "null";
            int tx = _targetTile?.GetX() ?? 0, tz = _targetTile?.GetZ() ?? 0;
            BoamBridge.Logger.Msg($"[BOAM] AttackStart: {uuid} {skillName} → ({tx},{tz}) duration={_attackDurationInSec}s");
        }
        catch { }
    }

    public static void OnActionPointsChangedHook(Actor _actor, int _oldAP, int _newAP)
    {
        try
        {
            if (!IsActive) return;
            var info = ActorRegistry.GetActorInfo(_actor);
            var uuid = info.HasValue ? ActorRegistry.GetUuid(info.Value.entityId) : "?";
            BoamBridge.Logger.Msg($"[BOAM] AP: {uuid} {_oldAP} → {_newAP}");
        }
        catch { }
    }

    public static void OnPlayerEndTurn()
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsEngineReady || !IsActive) return;

            int factionId = TacticalController.GetCurrentFaction();
            if (factionId != (int)Menace.SDK.FactionType.Player && factionId != (int)Menace.SDK.FactionType.PlayerAI) return;

            var activeGameObj = TacticalController.GetActiveActor();
            if (activeGameObj.IsNull) return;

            var actor = new Actor(activeGameObj.Pointer);
            var actorInfo = ActorRegistry.GetActorInfo(actor);
            if (actorInfo == null) return;
            var (gameObj, _, entityId, _) = actorInfo.Value;
            var actorUuid = ActorRegistry.GetUuid(entityId);

            int round = bridge.Round;
            if (entityId == _lastActorId && round == _lastRound) return;
            _lastActorId = entityId;
            _lastRound = round;

            var (tileX, tileZ) = ActorRegistry.GetPos(gameObj);
            var payload = JsonSerializer.Serialize(new
            {
                hook = "player-action", round, faction = factionId, actor = actorUuid,
                actionType = "endturn", skillName = "", tile = new { x = tileX, z = tileZ }
            });
            BoamBridge.Logger.Msg($"[BOAM] player-action {actorUuid}: endturn at ({tileX},{tileZ})");
            QueryCommandClient.Hook("player-action", payload);
        }
        catch (Exception ex) { BoamBridge.Logger.Error($"[BOAM] endturn patch error: {ex.Message}"); }
    }

    public static void OnClickOnTile(object __instance, Tile _tile, Actor _activeActor)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsEngineReady || !IsActive) return;

            int factionId = TacticalController.GetCurrentFaction();
            if (factionId != 1 && factionId != 2) return;

            int tileX = 0, tileZ = 0;
            try { if (_tile != null) { tileX = _tile.GetX(); tileZ = _tile.GetZ(); } } catch { }

            string actorUuid = "";
            if (_activeActor != null)
            {
                var info = ActorRegistry.GetActorInfo(_activeActor);
                if (info.HasValue) actorUuid = ActorRegistry.GetUuid(info.Value.entityId);
            }

            var payload = JsonSerializer.Serialize(new
            {
                hook = "player-action", round = bridge.Round, faction = factionId, actor = actorUuid,
                actionType = "click", skillName = "", tile = new { x = tileX, z = tileZ }
            });
            BoamBridge.Logger.Msg($"[BOAM] player-action {actorUuid}: click ({tileX},{tileZ})");
            QueryCommandClient.Hook("player-action", payload);
        }
        catch { }
    }

    public static void OnSelectSkill(object __instance, Il2CppMenace.Tactical.Skills.Skill _skill, bool __result)
    {
        try
        {
            if (!__result) return;
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsEngineReady || !IsActive) return;

            int factionId = TacticalController.GetCurrentFaction();
            if (factionId != 1 && factionId != 2) return;

            var skillName = "";
            try { skillName = _skill?.GetTitle() ?? ""; } catch { }

            string actorUuid = "";
            var activeActor = TacticalController.GetActiveActor();
            if (!activeActor.IsNull)
            {
                var info = ActorRegistry.GetActorInfo(new Actor(activeActor.Pointer));
                if (info.HasValue) actorUuid = ActorRegistry.GetUuid(info.Value.entityId);
            }

            var payload = JsonSerializer.Serialize(new
            {
                hook = "player-action", round = bridge.Round, faction = factionId, actor = actorUuid,
                actionType = "useskill", skillName, tile = new { x = 0, z = 0 }
            });
            StartPlayerSkillTimer(actorUuid, skillName);
            BoamBridge.Logger.Msg($"[BOAM] player-action {actorUuid}: useskill {skillName}");
            QueryCommandClient.Hook("player-action", payload);
        }
        catch { }
    }
}
