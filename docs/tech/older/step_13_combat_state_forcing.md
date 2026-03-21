# Step 13: Combat State Forcing

## Status: Phase 1 logging + Phase 2 forcing IMPLEMENTED — first successful deterministic replay achieved

### Golden Test

**Recording**: `battle_2026_03_19_00_11` — 2 rounds, 15 element hits, 31 AI turns
**Replay result**: `determinism=stop` → **Replay complete, zero divergences**

All three forcing layers active:
- Turn order (score rigging via `GetScoreMultForPickingThisAgent` + consume via `OnTurnEnd`)
- AI decision forcing (behavior + target at `Agent.Execute` Prefix)
- Element hit forcing (damage per model via `Element.OnHit` Prefix + missed hits via `ApplyMissedElementHits`)
- **Unit state forcing** (suppression, morale, armor via `SetSuppression`/`SetMorale`/`SetArmorDurability` after each burst)

**Extended recording**: `battle_2026_03_19_00_22` — 6 rounds, 437 lines, 84 element hits, 110 AI turns.
**Extended replay result**: `determinism=stop` → halted at decision #1013 (round 2).

However: the halt was a **false positive from the decision watchdog**, not an actual action divergence. The decision queue (from `ai_decisions.jsonl`) desynced — it expected `worker.1 → Move` at index #1013 but the actual Execute was `stinger.1 → Move`. But stinger.1 moved to `(9,29)` — the exact same tile as the recording. The **action was correct**, only the decision queue index was wrong.

### Key Insight: Decision Forcing May Be Unnecessary

The decision watchdog compares `Agent.Execute` calls against the sequential `ai_decisions.jsonl` queue. This queue has ~69 entries per actor turn (mostly InflictDamage polling re-evaluations) and desyncs when the iteration count differs between recording and replay.

But with turn order + element hit + unit state forcing, the game state is sufficiently deterministic that the AI naturally picks the correct behavior. The decision forcing via `Agent.Execute` Prefix may be unnecessary noise.

### Findings from Extended Testing

**Decision forcing IS needed.** Without it, the AI picks completely different behaviors (InflictDamage instead of Move for the blaster bug in round 1). Game state forcing alone is not sufficient — the AI's tile evaluation still produces different behavior rankings.

**Decision-level watchdog replaced by action-level watchdog.** The old watchdog compared every `Agent.Execute` call (7601 per battle, mostly polling). New watchdog compares only `ai_move` and `ai_useskill` events. Endturns are skipped (fire in different order due to event timing but don't indicate real divergence).

**Morale state transitions trigger interrupts.** Setting `SetMorale(0)` triggers `InvokeOnMoraleStateChanged` which causes the game to inject a panic-flee turn for the broken unit, bypassing pick order. **Fix:** set `m_Morale` and `m_Suppression` as fields directly (not via setter methods) and set `m_LastMoraleState` to prevent transition detection. Confirmed working.

**Move target tile not in `agent.m_Tiles`.** When the AI's `Evaluate()` produces different scored tiles (due to subtle game state differences — unit positions, AP, blocking), the recorded target tile isn't in the scored set. We can't set `m_TargetTile`, so the Move behavior fails or goes to the wrong destination. This is the current blocking issue.

### Next Steps

1. **Teleport on failed move**: after `OnTurnEnd`, if the actor was supposed to move but ended at the wrong tile, teleport to the recorded destination. This corrects the position even when the Move behavior fails internally.
2. **Action-level correction**: instead of only halting on divergence, correct and continue — teleport on wrong move, skip unexpected actions, inject missing ones.
3. **Save determinism**: confirmed stable within the same game build. Cross-build position differences observed (3/29 actors) — not a concern for same-session record/replay.
4. **Inter-faction ordering**: the game interleaves faction turns (civilian, wildlife, civilian, wildlife...). If the interleaving order differs between recording and replay, factions see different game state. The action watchdog currently uses a global stream and detects cross-faction misordering. Investigation needed: is the inter-faction order deterministic (fixed faction iteration order per frame), or does it vary? If variable, we need to force it (e.g. control which faction's `Process()` runs first each frame).
5. **Teleportation working**: `Entity.SetTile()` successfully corrects actor positions when Move behavior fails (target tile not in `agent.m_Tiles`). 5 teleports observed in a single replay. Move divergences are logged but don't halt the watchdog.
6. **m_LastMoraleState**: setting morale via field (`m_Morale`) instead of setter (`SetMorale()`) plus pre-setting `m_LastMoraleState` prevents the game from detecting morale transitions and triggering panic-flee interrupts. Same approach needed for suppression (`m_Suppression` field instead of `SetSuppression()`).

Step 12 built the forcing infrastructure (decision forcing, element hit forcing, turn order forcing) but replays still diverge because **combat side effects beyond HP damage are not captured or forced**. The game's combat resolution produces suppression, morale changes, and other state changes that affect AI behavior. Without forcing these, units make different decisions (e.g. Idle instead of Move when suppressed) and the replay diverges.

## What Step 12 Achieved

Working but insufficient:
- **AI decision forcing**: force `m_ActiveBehavior` + target at `Agent.Execute` Prefix
- **Element hit forcing**: zero wrong-element damage via `Element.OnHit` Prefix, apply missed hits after burst via `SetHitpoints`
- **Turn order forcing**: rig `GetScoreMultForPickingThisAgent` scores, consume via `OnTurnEnd`

## What's Missing

Each attack produces side effects beyond HP damage that we do NOT capture or force:

### Must log and force

| Event | Signature | What it does |
|-------|-----------|-------------|
| `InvokeOnSuppressionApplied` | `(Actor, float _change, Entity _suppressor)` | Adds suppression to a unit — suppressed units skip actions or pick Idle |
| `InvokeOnMoraleStateChanged` | `(Actor, MoraleState _moraleState)` | Changes morale state — broken units flee, panicked units act erratically |
| `InvokeOnHitpointsChanged` | `(Entity, float _hitpointsPct, int _animationDurationInMs)` | Entity HP % change notification — may trigger UI/AI reactions |
| `InvokeOnArmorChanged` | `(Entity, float _armorDurability, int _armor, int _animationDurationInMs)` | Armor durability change — affects future damage calculations |
| `InvokeOnActorStateChanged` | `(Actor, ActorState _oldState, ActorState _newState)` | Actor state transitions (normal → suppressed → panicked → broken) |
| `InvokeOnElementDeath` | `(Entity, Element, Entity _attacker, DamageInfo)` | Individual model death — affects squad capability |

### May need to log

| Event | Signature | Why |
|-------|-----------|-----|
| `InvokeOnDiscovered` | `(Entity, Actor _discoverer)` | Visibility state change — affects AI targeting |
| `InvokeOnVisibleToPlayer` | `(Actor)` | Fog of war state |
| `InvokeOnHiddenToPlayer` | `(Actor)` | Fog of war state |

## Objective

**Phase 1 — Logging (DONE)**: Extended `element_hit` log entries to include full unit state snapshot after each projectile impact. No new patches needed — all state read from existing Actor/Element accessors inside the `Element.OnHit` postfix.

Each `element_hit` entry now captures:
- **Element level**: `element_index`, `damage`, `element_hp_after`, `element_hp_max`, `element_alive`
- **Unit level**: `unit_hp`, `unit_hp_max`, `unit_ap`, `unit_suppression`, `unit_morale`, `unit_morale_state`, `unit_suppression_state`, `unit_armor_durability`

This gives per-projectile snapshots of the full combat state. During replay, comparing these values between recording and replay will show exactly what diverged and when.

**Phase 2 — Forcing**: Once we can see the state delta, force the recorded values during replay. Approach: after each element hit is forced (Phase 1 from step 12), also set `Actor.SetSuppression()`, `Actor.SetMorale()`, `Actor.SetArmorDurability()`, `Actor.SetActionPoints()` to match the recorded unit state snapshot. This ensures the AI sees the same game state when making decisions.

## Root Cause Example

From `battle_2026_03_18_22_06` replay:
1. Stinger.3 attacks worker.1 — element hit forcing applies correct damage
2. But suppression from the attack differs (RNG-dependent) — worker.1 becomes suppressed in replay but wasn't in recording (or vice versa)
3. When worker.1's turn comes, it picks Idle (suppressed) instead of Move
4. Turn order queue expects worker.1 later but it's already done — queue desyncs
5. All subsequent actor order diverges

## Files to Modify

- `src/AiActionPatches.cs` — add patches for suppression, morale, armor, actor state events
- `src/BoamBridge.cs` — register new patches
- `boam_tactical_engine/GameTypes.fs` — new payload types
- `boam_tactical_engine/HookPayload.fs` — new parsers
- `boam_tactical_engine/ActionLog.fs` — new log writers
- `boam_tactical_engine/Routes.fs` — new hook routes
- `src/ReplayForcing.cs` — forcing logic (Phase 2)
