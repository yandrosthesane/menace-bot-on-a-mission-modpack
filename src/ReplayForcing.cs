using System;
using System.Collections.Generic;
using System.Text.Json;
using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.AI;
using Il2CppMenace.Tactical.AI.Behaviors;
using MelonLoader;
using Menace.SDK;

namespace BOAM;

/// <summary>
/// Replay forcing state: holds expected AI decisions and element hits.
/// Populated on replay start, consumed during replay.
/// </summary>
static class ReplayForcing
{
    // ── AI decision forcing ──

    public struct ExpectedDecision
    {
        public string Actor;
        public int BehaviorId;
        public string ChosenName;
        public int TargetX;
        public int TargetZ;
        public int Round;
    }

    private static Queue<ExpectedDecision> _decisions = new();

    // ── Element hit forcing ──

    public struct ExpectedElementHit
    {
        public string Target;       // actor UUID
        public string Attacker;
        public string Skill;
        public int ElementIndex;
        public int Damage;
        public int ElementHpAfter;
        public bool ElementAlive;
        public int Round;
    }

    private static Queue<ExpectedElementHit> _elementHits = new();

    /// <summary>
    /// Parse forcing data fetched from the engine.
    /// </summary>
    public static void LoadFromReplayStart(string json)
    {
        _decisions.Clear();
        _elementHits.Clear();

        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("ai_decisions", out var decisions))
            {
                foreach (var d in decisions.EnumerateArray())
                {
                    _decisions.Enqueue(new ExpectedDecision
                    {
                        Actor = d.TryGetProperty("actor", out var a) ? a.GetString() ?? "" : "",
                        BehaviorId = d.TryGetProperty("behavior_id", out var b) ? b.GetInt32() : 0,
                        ChosenName = d.TryGetProperty("chosen_name", out var n) ? n.GetString() ?? "" : "",
                        TargetX = d.TryGetProperty("target_x", out var tx) ? tx.GetInt32() : 0,
                        TargetZ = d.TryGetProperty("target_z", out var tz) ? tz.GetInt32() : 0,
                        Round = d.TryGetProperty("round", out var r) ? r.GetInt32() : 0,
                    });
                }
            }

            if (root.TryGetProperty("element_hits", out var hits))
            {
                foreach (var h in hits.EnumerateArray())
                {
                    _elementHits.Enqueue(new ExpectedElementHit
                    {
                        Target = h.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "",
                        Attacker = h.TryGetProperty("attacker", out var att) ? att.GetString() ?? "" : "",
                        Skill = h.TryGetProperty("skill", out var s) ? s.GetString() ?? "" : "",
                        ElementIndex = h.TryGetProperty("element_index", out var ei) ? ei.GetInt32() : 0,
                        Damage = h.TryGetProperty("damage", out var dmg) ? dmg.GetInt32() : 0,
                        ElementHpAfter = h.TryGetProperty("element_hp_after", out var hp) ? hp.GetInt32() : 0,
                        ElementAlive = h.TryGetProperty("element_alive", out var al) ? al.GetBoolean() : true,
                        Round = h.TryGetProperty("round", out var r) ? r.GetInt32() : 0,
                    });
                }
            }

            BoamBridge.Logger.Msg($"[BOAM] ReplayForcing loaded: {_decisions.Count} AI decisions, {_elementHits.Count} element hits");
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Error($"[BOAM] ReplayForcing parse error: {ex.Message}");
        }
    }

    public static void Clear()
    {
        _decisions.Clear();
        _elementHits.Clear();
    }

    public static bool HasDecisions => _decisions.Count > 0;
    public static bool HasElementHits => _elementHits.Count > 0;

    // ── Decision forcing API ──

    /// <summary>
    /// Try to force the next AI decision. Called from Patch_AgentExecute Prefix.
    /// </summary>
    public static bool TryForceDecision(Agent agent)
    {
        if (_decisions.Count == 0) return false;

        var actor = agent.m_Actor;
        if (actor == null) return false;

        var info = ActorRegistry.GetActorInfo(actor);
        if (info == null) return false;
        var actorUuid = ActorRegistry.GetUuid(info.Value.entityId);

        var expected = _decisions.Peek();
        if (expected.Actor != actorUuid)
            return false;

        _decisions.Dequeue();

        var behaviors = agent.GetBehaviors();
        if (behaviors == null) return false;

        Behavior matchedBehavior = null;
        for (int i = 0; i < behaviors.Count; i++)
        {
            var b = behaviors[i];
            if (b != null && (int)b.GetID() == expected.BehaviorId)
            {
                matchedBehavior = b;
                break;
            }
        }

        if (matchedBehavior == null)
        {
            BoamBridge.Logger.Warning($"[BOAM] FORCE: behavior {expected.BehaviorId} ({expected.ChosenName}) not found for {actorUuid}");
            return false;
        }

        agent.m_ActiveBehavior = matchedBehavior;

        // Set target tile for Move behavior
        try
        {
            var moveBehavior = matchedBehavior.TryCast<Move>();
            if (moveBehavior != null && expected.TargetX != 0 && expected.TargetZ != 0)
            {
                var tiles = agent.m_Tiles;
                if (tiles != null)
                {
                    var enumerator = tiles.GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        var kvp = enumerator.Current;
                        if (kvp.Key != null && kvp.Key.GetX() == expected.TargetX && kvp.Key.GetZ() == expected.TargetZ)
                        {
                            moveBehavior.m_TargetTile = kvp.Value;
                            break;
                        }
                    }
                }
            }
        }
        catch { }

        // Set target tile for SkillBehavior (attack, etc.)
        try
        {
            var skillBehavior = matchedBehavior.TryCast<Il2CppMenace.Tactical.AI.SkillBehavior>();
            if (skillBehavior != null && expected.TargetX != 0 && expected.TargetZ != 0)
            {
                var tileGameObj = TileMap.GetTile(expected.TargetX, expected.TargetZ);
                if (!tileGameObj.IsNull)
                {
                    var tile = new Tile(tileGameObj.Pointer);
                    skillBehavior.m_TargetTile = tile;
                }
            }
        }
        catch { }

        BoamBridge.Logger.Msg($"[BOAM] FORCE: {actorUuid} → {expected.ChosenName}({expected.BehaviorId}) @({expected.TargetX},{expected.TargetZ})");
        return true;
    }

    // ── Element hit forcing API ──

    /// <summary>
    /// Called from Element.OnHit Postfix during replay.
    /// Corrects the element's HP to match the recorded value.
    /// If the hit queue is empty or doesn't match, logs a warning.
    /// </summary>
    public static void ForceElementHit(Il2CppMenace.Tactical.Element element, string targetUuid, string attackerUuid, int elementIndex)
    {
        if (_elementHits.Count == 0) return;

        var expected = _elementHits.Peek();

        // Match by target + attacker + elementIndex
        if (expected.Target != targetUuid || expected.Attacker != attackerUuid || expected.ElementIndex != elementIndex)
        {
            BoamBridge.Logger.Warning($"[BOAM] FORCE HIT MISMATCH: expected {expected.Attacker}→{expected.Target}[{expected.ElementIndex}] got {attackerUuid}→{targetUuid}[{elementIndex}]");
            return;
        }

        _elementHits.Dequeue();

        int actualHp = element.GetHitpoints();
        if (actualHp != expected.ElementHpAfter)
        {
            element.SetHitpoints(expected.ElementHpAfter);
            BoamBridge.Logger.Msg($"[BOAM] FORCE HIT: {targetUuid}[{elementIndex}] hp={actualHp}→{expected.ElementHpAfter} (expected dmg={expected.Damage})");
        }
    }
}
