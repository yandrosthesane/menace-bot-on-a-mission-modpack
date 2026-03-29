using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Il2CppMenace.Tactical;
using MelonLoader;
using Menace.SDK;

namespace BOAM.GameEvents;

static class OnTurnEndEvent
{
    internal static bool IsActive => Boundary.GameEvents.OnTurnEnd;

    internal delegate void Enrichment(GameObj gameObj, Actor actor, Entity entity, int vision, int factionId, Dictionary<string, object> payload);

    private static readonly List<Enrichment> _enrichments = new();

    private static readonly Dictionary<string, Enrichment> _enrichmentRegistry = new()
    {
        ["contact-state"] = (go, a, e, v, f, p) => ContactStateEvent.Enrich(go, v, f, p),
        ["movement-budget"] = (go, a, e, v, f, p) => MovementBudgetEvent.Enrich(a, e, p),
    };

    internal static void InitEnrichments()
    {
        _enrichments.Clear();
        if (Boundary.GameEvents.Enrichments.TryGetValue("on-turn-end", out var names))
        {
            foreach (var name in names)
                if (_enrichmentRegistry.TryGetValue(name, out var cb))
                    _enrichments.Add(cb);
        }
    }

    internal static void Register(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
    {
        try
        {
            var tmType = typeof(TacticalManager);
            var m = Array.Find(tmType.GetMethods(), m => m.Name == "InvokeOnTurnEnd" && m.GetParameters().Length == 1);
            if (m != null)
            {
                harmony.Patch(m, postfix: new HarmonyLib.HarmonyMethod(typeof(OnTurnEndEvent), nameof(OnTurnEnd)));
                log.Msg("[BOAM] Patched InvokeOnTurnEnd");
            }
        }
        catch (Exception ex)
        {
            log.Error($"[BOAM] Failed to patch OnTurnEnd: {ex.Message}");
        }
    }

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

            var bridge = BoamBridge.Instance;
            if (bridge == null || !bridge.IsEngineReady) return;
            if (!IsActive) return;

            BoamBridge.Logger.Msg($"[BOAM] TurnEnd: {actorUuid} f{factionId}");

            int round = bridge.Round;

            var entity = new Entity(gameObj.Pointer);
            var actor = new Actor(gameObj.Pointer);
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
            try { vision = LineOfSight.GetVision(gameObj); } catch { }
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

            var turnEndData = new Dictionary<string, object>
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
                }
            };

            foreach (var enrich in _enrichments)
                enrich(gameObj, actor, entity, vision, factionId, turnEndData);

            var turnEndPayload = JsonSerializer.Serialize(turnEndData);
            TileModifiersEvent.SetPending();
            ThreadPool.QueueUserWorkItem(_ => QueryCommandClient.SendEvent("on-turn-end", turnEndPayload));

            // AI action logging (AI factions only)
            ActionLoggingEvent.LogAiEndTurn(round, factionId, actorUuid, tileX, tileZ);
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] endturn error: {ex.Message}");
        }
    }
}
