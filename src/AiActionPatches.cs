using System;
using System.Text.Json;
using System.Threading;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;
using MelonLoader;
using Menace.SDK;

namespace BOAM;

/// <summary>
/// Patches on TacticalManager.InvokeOn* to capture actual AI actions (move, skill use, end turn)
/// and combat outcomes (damage, miss, kill) for ALL factions.
/// AI actions are filtered to non-player factions. Combat outcomes log everything.
/// </summary>
static class AiActionPatches
{
    /// <summary>
    /// Returns true if the faction is AI-controlled (not player).
    /// Player factions (1=Player, 2=PlayerAI) are excluded — they have their own logging.
    /// </summary>
    private static bool IsAiFaction(int factionId) =>
        factionId != (int)Menace.SDK.FactionType.Player
        && factionId != (int)Menace.SDK.FactionType.PlayerAI;

    /// <summary>
    /// InvokeOnMovementFinished(Actor, Tile) — actor finished moving to a tile.
    /// </summary>
    public static void OnMovementFinished(Actor _actor, Tile _to)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsReady) return;
            if (_actor == null || _to == null) return;

            var actorInfo = ActorRegistry.GetActorInfo(_actor);
            if (actorInfo == null) return;
            var (_, factionId, entityId, _) = actorInfo.Value;
            if (!IsAiFaction(factionId)) return;

            var actorUuid = ActorRegistry.GetUuid(entityId);
            int tileX = _to.GetX();
            int tileZ = _to.GetZ();

            var payload = JsonSerializer.Serialize(new
            {
                hook = "ai-action",
                round = bridge.Round,
                faction = factionId,
                actor = actorUuid,
                actionType = "ai_move",
                skillName = "",
                tile = new { x = tileX, z = tileZ }
            });

            BoamBridge.Logger.Msg($"[BOAM] ai-action {actorUuid}: ai_move ({tileX},{tileZ})");
            ThreadPool.QueueUserWorkItem(_ => EngineClient.Post("/hook/ai-action", payload));
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] ai-action move error: {ex.Message}");
        }
    }

    /// <summary>
    /// InvokeOnSkillUse(Actor, Skill, Tile) — actor used a skill on a target tile.
    /// </summary>
    public static void OnSkillUse(Actor _actor, Skill _skill, Tile _targetTile)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsReady) return;
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
                hook = "ai-action",
                round = bridge.Round,
                faction = factionId,
                actor = actorUuid,
                actionType = "ai_useskill",
                skillName,
                tile = new { x = tileX, z = tileZ }
            });

            BoamBridge.Logger.Msg($"[BOAM] ai-action {actorUuid}: ai_useskill {skillName} ({tileX},{tileZ})");
            ThreadPool.QueueUserWorkItem(_ => EngineClient.Post("/hook/ai-action", payload));
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] ai-action useskill error: {ex.Message}");
        }
    }

    /// <summary>
    /// InvokeOnTurnEnd(Actor) — actor's turn ended.
    /// </summary>
    public static void OnTurnEnd(Actor _actor)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsReady) return;
            if (_actor == null) return;

            var actorInfo = ActorRegistry.GetActorInfo(_actor);
            if (actorInfo == null) return;
            var (gameObj, factionId, entityId, _) = actorInfo.Value;
            if (!IsAiFaction(factionId)) return;

            var actorUuid = ActorRegistry.GetUuid(entityId);
            var (tileX, tileZ) = ActorRegistry.GetPos(gameObj);

            var payload = JsonSerializer.Serialize(new
            {
                hook = "ai-action",
                round = bridge.Round,
                faction = factionId,
                actor = actorUuid,
                actionType = "ai_endturn",
                skillName = "",
                tile = new { x = tileX, z = tileZ }
            });

            BoamBridge.Logger.Msg($"[BOAM] ai-action {actorUuid}: ai_endturn");
            ThreadPool.QueueUserWorkItem(_ => EngineClient.Post("/hook/ai-action", payload));
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] ai-action endturn error: {ex.Message}");
        }
    }

    // ── Per-element hit logging (ALL factions) ──
    //
    // Entity.OnElementHit is the atomic combat operation: one projectile hits one element (model)
    // within a squad for a specific amount of damage. Recording and replaying these exactly
    // produces deterministic combat outcomes — deaths are a natural consequence of HP reaching 0.

    /// <summary>
    /// Element.OnHit(Entity _attacker, DamageInfo, int _damageAppliedToElement, Skill)
    /// Per-projectile, per-model hit. The atomic unit of combat.
    /// __instance is the Element that was hit.
    /// </summary>
    public static void OnElementHit(Il2CppMenace.Tactical.Element __instance,
        Entity _attacker, Il2CppMenace.Tactical.DamageInfo _damageInfo,
        int _damageAppliedToElement, Skill _skill)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsReady) return;
            if (__instance == null) return;

            var entity = __instance.GetEntity();
            if (entity == null) return;
            var targetActor = entity.TryCast<Actor>();
            if (targetActor == null) return;
            var targetInfo = ActorRegistry.GetActorInfo(targetActor);
            if (targetInfo == null) return;
            var (_, targetFaction, targetEntityId, _) = targetInfo.Value;
            var targetUuid = ActorRegistry.GetUuid(targetEntityId);

            int elementIndex = __instance.GetElementIndex();

            string attackerUuid = "";
            int attackerFaction = 0;
            if (_attacker != null)
            {
                var attackerActor = _attacker.TryCast<Actor>();
                if (attackerActor != null)
                {
                    var attackerInfo = ActorRegistry.GetActorInfo(attackerActor);
                    if (attackerInfo != null)
                    {
                        attackerFaction = attackerInfo.Value.factionId;
                        attackerUuid = ActorRegistry.GetUuid(attackerInfo.Value.entityId);
                    }
                }
            }

            var skillName = "";
            try { skillName = _skill?.GetTitle() ?? ""; } catch { }

            int elementHpAfter = __instance.GetHitpoints();
            bool elementAlive = __instance.IsAlive();

            var payload = JsonSerializer.Serialize(new
            {
                hook = "combat-outcome",
                round = bridge.Round,
                type = "element_hit",
                target = targetUuid,
                targetFaction,
                attacker = attackerUuid,
                attackerFaction,
                skill = skillName,
                elementIndex,
                damage = _damageAppliedToElement,
                elementHpAfter,
                elementAlive
            });

            BoamBridge.Logger.Msg($"[BOAM] element_hit {attackerUuid} → {targetUuid}[{elementIndex}]: {_damageAppliedToElement}dmg hp={elementHpAfter} alive={elementAlive}");
            ThreadPool.QueueUserWorkItem(_ => EngineClient.Post("/hook/combat-outcome", payload));

            // Replay forcing: correct element HP to match recording
            if (bridge._replayActive && ReplayForcing.HasElementHits)
            {
                ReplayForcing.ForceElementHit(__instance, targetUuid, attackerUuid, elementIndex);
            }
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] element_hit error: {ex.Message}");
        }
    }
}
