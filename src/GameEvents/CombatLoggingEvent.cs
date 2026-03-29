using System;
using System.Text.Json;
using System.Threading;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;
using MelonLoader;

namespace BOAM.GameEvents;

static class CombatLoggingEvent
{
    internal static bool IsActive => Boundary.GameEvents.CombatLogging;

    internal static void Register(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
    {
        try
        {
            var elementType = typeof(Element);
            var m = Array.Find(elementType.GetMethods(), m => m.Name == "OnHit");
            if (m != null)
            {
                harmony.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(CombatLoggingEvent), nameof(OnElementHit)));
                log.Msg("[BOAM] Patched Element.OnHit");
            }
        }
        catch (Exception ex)
        {
            log.Error($"[BOAM] Failed to patch Element.OnHit: {ex.Message}");
        }
    }

    public static void OnElementHit(Element __instance,
        Entity _attacker, DamageInfo _damageInfo,
        int _damageAppliedToElement, Skill _skill)
    {
        try
        {
            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsEngineReady) return;
            if (!IsActive) return;
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
            int elementHpMax = __instance.GetHitpointsMax();
            bool elementAlive = __instance.IsAlive();

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

            BoamBridge.Logger.Msg($"[BOAM] element_hit {attackerUuid} → {targetUuid}[{elementIndex}]: {_damageAppliedToElement}dmg");
            ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.SendEvent("combat-outcome", payload));
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] element_hit error: {ex.Message}");
        }
    }
}
