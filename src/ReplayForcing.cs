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
        _currentBurstKey = "";
        _currentBurstDamageByElement.Clear();
        _missedElements.Clear();
    }

    public static bool HasDecisions => _decisions.Count > 0;
    public static bool HasElementHits => _elementHits.Count > 0;
    public static bool HasPendingMissedHits => _missedElements.Count > 0;

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

    // ── Element hit forcing (per-hit) ──
    //
    // During replay, Element.OnHit Prefix overrides _damageAppliedToElement:
    // - If this (attacker, target, elementIndex) matches a recorded hit: use recorded damage
    // - Otherwise: set damage to 0 (nullify the hit)
    //
    // Hits are grouped by (attacker, target) burst. When a new burst starts,
    // the recorded hits for that burst are loaded into a lookup by elementIndex.

    private static string _currentBurstKey = "";
    private static Dictionary<int, int> _currentBurstDamageByElement = new();
    // Elements that have recorded damage but were NOT hit by the game during the burst.
    // Populated when a burst loads, entries removed when the game hits the right element.
    private static Dictionary<int, int> _missedElements = new();

    /// <summary>
    /// Get the forced damage for a specific element during replay.
    /// Returns recorded damage if this element was hit in recording, 0 otherwise.
    /// </summary>
    public static int GetForcedDamage(string attackerUuid, string targetUuid, int elementIndex)
    {
        string burstKey = $"{attackerUuid}→{targetUuid}";

        // If this is a new burst, load the recorded hits for it
        if (burstKey != _currentBurstKey)
        {
            // Before switching burst, any remaining _missedElements from previous burst
            // should have been applied by ApplyMissedElementHits already.
            _currentBurstKey = burstKey;
            _currentBurstDamageByElement.Clear();
            _missedElements.Clear();

            // Consume all consecutive hits from the queue that match this burst
            while (_elementHits.Count > 0)
            {
                var next = _elementHits.Peek();
                if (next.Attacker == attackerUuid && next.Target == targetUuid)
                {
                    if (!_currentBurstDamageByElement.ContainsKey(next.ElementIndex))
                        _currentBurstDamageByElement[next.ElementIndex] = next.Damage;
                    else
                        _currentBurstDamageByElement[next.ElementIndex] += next.Damage;
                    _elementHits.Dequeue();
                }
                else
                {
                    break; // Next burst
                }
            }

            // All recorded elements start as "missed" — removed when the game actually hits them
            foreach (var kvp in _currentBurstDamageByElement)
                _missedElements[kvp.Key] = kvp.Value;

            BoamBridge.Logger.Msg($"[BOAM] FORCE BURST LOADED: {burstKey} → {_currentBurstDamageByElement.Count} elements");
        }

        // Look up recorded damage for this element
        if (_currentBurstDamageByElement.TryGetValue(elementIndex, out var recordedDamage))
        {
            // Game hit the right element — remove from missed set
            _missedElements.Remove(elementIndex);
            return recordedDamage;
        }

        // Element wasn't hit in recording — zero damage
        return 0;
    }

    /// <summary>
    /// Called from InvokeOnAttackTileStart to preload the burst for an attacker→target pair.
    /// Ensures the burst is loaded even if no Element.OnHit fires (all misses).
    /// </summary>
    public static void PreloadBurst(string attackerUuid, string targetUuid)
    {
        string burstKey = $"{attackerUuid}→{targetUuid}";
        if (burstKey == _currentBurstKey) return; // already loaded

        // Apply any missed elements from the previous burst before switching
        if (_missedElements.Count > 0)
            ApplyMissedElementHits();

        _currentBurstKey = burstKey;
        _currentBurstDamageByElement.Clear();
        _missedElements.Clear();

        while (_elementHits.Count > 0)
        {
            var next = _elementHits.Peek();
            if (next.Attacker == attackerUuid && next.Target == targetUuid)
            {
                if (!_currentBurstDamageByElement.ContainsKey(next.ElementIndex))
                    _currentBurstDamageByElement[next.ElementIndex] = next.Damage;
                else
                    _currentBurstDamageByElement[next.ElementIndex] += next.Damage;
                _elementHits.Dequeue();
            }
            else
            {
                break;
            }
        }

        foreach (var kvp in _currentBurstDamageByElement)
            _missedElements[kvp.Key] = kvp.Value;

        if (_currentBurstDamageByElement.Count > 0)
            BoamBridge.Logger.Msg($"[BOAM] FORCE BURST PRELOADED: {burstKey} → {_currentBurstDamageByElement.Count} elements");
    }

    /// <summary>
    /// Called after each skill use completes (InvokeOnAfterSkillUse).
    /// Applies recorded damage to elements that SHOULD have been hit but weren't
    /// (game's RNG picked different elements). Uses SetHitpoints to subtract damage.
    /// </summary>
    public static void ApplyMissedElementHits()
    {
        if (_currentBurstDamageByElement.Count == 0) return;

        // Any remaining entries in _currentBurstDamageByElement are elements that
        // had recorded damage but were never hit by the game during this burst.
        // (Consumed entries are removed by GetForcedDamage when matched.)
        // Wait — GetForcedDamage doesn't remove entries. It looks them up.
        // We need a separate "applied" set to track which elements were actually hit.
        // For now, use the _appliedElements set populated during GetForcedDamage.

        if (_missedElements.Count == 0) return;

        // Parse burst key back to target UUID
        var parts = _currentBurstKey.Split('→');
        if (parts.Length != 2) return;
        var targetUuid = parts[1];

        try
        {
            var allActors = EntitySpawner.ListEntities(-1);
            if (allActors == null) return;

            foreach (var a in allActors)
            {
                var aInfo = EntitySpawner.GetEntityInfo(a);
                if (aInfo == null) continue;
                var uuid = ActorRegistry.GetUuid(aInfo.EntityId);
                if (uuid != targetUuid) continue;

                var actor = new Actor(a.Pointer);
                var elements = actor.GetElements();
                if (elements == null) break;

                foreach (var kvp in _missedElements)
                {
                    int elementIndex = kvp.Key;
                    int recordedDamage = kvp.Value;
                    if (elementIndex < elements.Count)
                    {
                        var element = elements[elementIndex];
                        if (element != null && element.IsAlive())
                        {
                            int currentHp = element.GetHitpoints();
                            int newHp = Math.Max(0, currentHp - recordedDamage);
                            element.SetHitpoints(newHp);
                            BoamBridge.Logger.Msg($"[BOAM] FORCE MISSED: {targetUuid}[{elementIndex}] hp={currentHp}→{newHp} (recorded dmg={recordedDamage})");
                        }
                    }
                }
                break;
            }
        }
        catch (Exception ex)
        {
            BoamBridge.Logger.Warning($"[BOAM] FORCE MISSED failed: {ex.Message}");
        }

        // Clear for next burst
        _missedElements.Clear();
    }
}
