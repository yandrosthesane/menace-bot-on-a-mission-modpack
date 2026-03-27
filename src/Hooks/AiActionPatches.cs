using System;
using System.Text.Json;
using System.Threading;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;
using Il2CppMenace.Tactical.Skills;
using MelonLoader;
using Menace.SDK;

namespace BOAM;

/// <summary>
/// Patches on TacticalManager.InvokeOn* to capture AI actions and combat outcomes.
/// Logging only — no forcing.
/// </summary>
static class AiActionPatches
{
    private static bool IsAiFaction(int factionId) =>
        factionId != (int)Menace.SDK.FactionType.Player
        && factionId != (int)Menace.SDK.FactionType.PlayerAI;

    /// <summary>
    /// Gathers movement cost table from the actor's MovementType template.
    /// Spreads results into the payload dictionary.
    /// </summary>
    private static void GatherMovementData(Actor actor, Entity entity, string actorUuid,
        System.Collections.Generic.Dictionary<string, object> payload)
    {
        try
        {
            var movType = entity.GetTemplate()?.MovementType;
            if (movType != null)
            {
                var costs = new System.Collections.Generic.List<int>();
                if (movType.m_MovementCosts != null)
                {
                    for (int i = 0; i < movType.m_MovementCosts.Length; i++)
                        costs.Add((int)movType.m_MovementCosts[i]);
                }
                payload["movement"] = new
                {
                    costs,
                    turningCost = (int)movType.m_TurningCost,
                    lowestMovementCost = movType.GetLowestMovementCost(),
                    isFlying = movType.Flying
                };
            }
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Warning($"[BOAM] MovementType read failed for {actorUuid}: {ex.Message}");
        }
    }

    /// <summary>
    /// InvokeOnMovementFinished(Actor, Tile) — AI actor finished moving.
    /// </summary>
    public static void OnMovementFinished(Actor _actor, Tile _to)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsEngineReady) return;
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
            ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("ai-action", payload));
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] ai-action move error: {ex.Message}");
        }
    }

    /// <summary>
    /// InvokeOnSkillUse(Actor, Skill, Tile) — AI actor used a skill.
    /// </summary>
    public static void OnSkillUse(Actor _actor, Skill _skill, Tile _targetTile)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsEngineReady) return;
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
            ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("ai-action", payload));
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] ai-action useskill error: {ex.Message}");
        }
    }

    /// <summary>
    /// InvokeOnTurnEnd(Actor) — any actor's turn ended (player + AI).
    /// Sends ai-action endturn for AI factions, and on-turn-end notification for all.
    /// </summary>
    public static void OnTurnEnd(Actor _actor)
    {
        try
        {
            if (_actor == null) return;

            var actorInfo = ActorRegistry.GetActorInfo(_actor);
            if (actorInfo == null) return;
            var (gameObj, factionId, entityId, _) = actorInfo.Value;
            var actorUuid = ActorRegistry.GetUuid(entityId);
            var (tileX, tileZ) = ActorRegistry.GetPos(gameObj);

            BoamBridge.Logger.Msg($"[BOAM] TurnEnd: {actorUuid} f{factionId}");

            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsEngineReady) return;

            int round = bridge.Round;

            // Gather actor status
            var entity = new Il2CppMenace.Tactical.Entity(gameObj.Pointer);
            var actor = new Il2CppMenace.Tactical.Actor(gameObj.Pointer);
            int ap = 0, apStart = 0, hp = 0, hpMax = 0, armor = 0, armorMax = 0;
            int vision = 0, concealment = 0;
            float morale = 0, moraleMax = 0, suppression = 0;
            bool isStunned = false, isDying = false, hasActed = false;
            try { ap = actor.GetActionPoints(); } catch { }
            try { apStart = actor.GetActionPointsAtTurnStart(); } catch { }
            try { hp = entity.GetHitpoints(); } catch { }
            try { hpMax = entity.GetHitpointsMax(); } catch { }
            try { armor = entity.GetArmorDurability(); } catch { }
            try { armorMax = entity.GetArmorDurabilityMax(); } catch { }
            try { vision = Menace.SDK.LineOfSight.GetVision(gameObj); } catch { }
            try { morale = actor.GetMorale(); } catch { }
            try { moraleMax = actor.GetMoraleMax(); } catch { }
            try { suppression = actor.GetSuppression(); } catch { }
            try { isStunned = actor.IsStunned(); } catch { }
            try { isDying = actor.IsDying(); } catch { }
            try { hasActed = actor.HasActed(); } catch { }
            try
            {
                var props = entity.GetTemplate()?.Properties;
                if (props != null) concealment = props.GetConcealment();
            }
            catch { }

            // Gather skills
            var skillList = new System.Collections.Generic.List<object>();
            try
            {
                var attacks = actor.GetAllAttacks();
                if (attacks != null)
                {
                    for (int i = 0; i < attacks.Count; i++)
                    {
                        var skill = attacks[i];
                        if (skill == null) continue;
                        skillList.Add(new
                        {
                            name = skill.GetID() ?? "",
                            apCost = skill.GetActionPointCost(),
                            minRange = skill.GetMinRange(),
                            maxRange = skill.GetMaxRange(),
                            idealRange = skill.GetIdealRange()
                        });
                    }
                }
            }
            catch { }

            // Build payload as dictionary so gatherers can spread fields into it
            var turnEndData = new System.Collections.Generic.Dictionary<string, object>
            {
                ["round"] = round,
                ["faction"] = factionId,
                ["actor"] = actorUuid,
                ["tile"] = new { x = tileX, z = tileZ },
                ["status"] = new
                {
                    ap, apStart, hp, hpMax, armor, armorMax,
                    vision, concealment,
                    morale, moraleMax, suppression,
                    isStunned, isDying, hasActed
                },
                ["skills"] = skillList
            };

            // Optional data gatherers spread their fields into the payload
            GatherMovementData(actor, entity, actorUuid, turnEndData);

            var turnEndPayload = JsonSerializer.Serialize(turnEndData);
            TileModifierStore.SetPending();
            ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("on-turn-end", turnEndPayload));

            // AI action logging (AI factions only)
            if (IsAiFaction(factionId))
            {
                var payload = JsonSerializer.Serialize(new
                {
                    hook = "ai-action",
                    round,
                    faction = factionId,
                    actor = actorUuid,
                    actionType = "ai_endturn",
                    skillName = "",
                    tile = new { x = tileX, z = tileZ }
                });
                ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("ai-action", payload));
            }
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] endturn error: {ex.Message}");
        }
    }

    // ── Per-element hit logging (ALL factions) ──

    /// <summary>
    /// Element.OnHit POSTFIX — logs per-projectile, per-model hit with full unit state.
    /// </summary>
    public static void OnElementHit(Il2CppMenace.Tactical.Element __instance,
        Entity _attacker, Il2CppMenace.Tactical.DamageInfo _damageInfo,
        int _damageAppliedToElement, Skill _skill)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsEngineReady) return;
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

            // Element-level state
            int elementHpAfter = __instance.GetHitpoints();
            int elementHpMax = __instance.GetHitpointsMax();
            bool elementAlive = __instance.IsAlive();

            // Unit-level state
            float unitSuppression = 0f;
            float unitMorale = 0f;
            int unitArmorDurability = 0;
            int unitHp = 0;
            int unitHpMax = 0;
            int unitAp = 0;
            int unitMoraleState = 0;
            int unitSuppressionState = 0;
            try
            {
                unitSuppression = targetActor.GetSuppression();
                unitMorale = targetActor.GetMorale();
                unitArmorDurability = targetActor.GetArmorDurability();
                unitHp = targetActor.GetHitpoints();
                unitHpMax = targetActor.GetHitpointsMax();
                unitAp = targetActor.GetActionPoints();
                unitMoraleState = (int)targetActor.GetMoraleState();
                unitSuppressionState = (int)targetActor.GetSuppressionState();
            }
            catch { }

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
                elementHpMax,
                elementAlive,
                unitHp,
                unitHpMax,
                unitAp,
                unitSuppression,
                unitMorale,
                unitMoraleState,
                unitSuppressionState,
                unitArmorDurability
            });

            BoamBridge.Logger.Msg($"[BOAM] element_hit {attackerUuid} → {targetUuid}[{elementIndex}]: {_damageAppliedToElement}dmg ehp={elementHpAfter} uhp={unitHp} sup={unitSuppression:F1} mor={unitMorale:F1} armor={unitArmorDurability}");
            ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.Hook("combat-outcome", payload));
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] element_hit error: {ex.Message}");
        }
    }
}
