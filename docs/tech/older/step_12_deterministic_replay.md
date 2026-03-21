# Step 12: Deterministic Replay

## Objective

Make replay produce identical results to manual play — same AI decisions, same outcomes. This enables:
- Replay verification (did the replay faithfully reproduce the battle?)
- AI behavior change impact analysis (modify AI, replay same battle, diff the decisions)

## Problems and Solutions

### 1. `select` command pollutes RNG

**Problem:** The original `ExecuteSelect` used `UnitsTurnBar.SelectNextActor()` to cycle through actors until finding the target. Each cycle call advances the game's RNG, causing all subsequent AI decisions to diverge.

**Solution:** Direct slot selection via portrait click simulation:
1. Find the target actor proxy by scanning `EntitySpawner.ListEntities()`
2. Call `UnitsTurnBar.GetSlot(actorProxy)` to get the slot directly (no cycling)
3. Call `UnitsTurnBarSlot.OnLeftClicked(slot)` to simulate the portrait click

**Key discovery:** In Il2Cpp, `m_Slots` is exposed as a property getter (`get_m_Slots()`) not a field. `GetSlot(Actor)` is public and works directly — no need to iterate.

**Class chain:** `Entity (abstract) → Actor (abstract) → UnitActor (concrete)`. No dedicated Vehicle class — vehicles are `UnitActor` instances with `IsVehicle()` returning true.

### 2. Bridge didn't handle `halted` replay status

**Problem:** When the determinism watchdog halts the replay (on first divergence), the engine returns `status: "halted"` from `/replay/next`. The bridge only handled `"done"` and `"waiting"`, so it fell through and tried to parse action fields that didn't exist, continuing to pull.

**Solution:** Added `"halted"` status handling in `BoamBridge.cs` — sets `_replayActive = false`, logs the reason and divergence count, shows a toast.

### 3. Production vs dev script divergence

**Problem:** The dev `scripts/start-tactical-engine.sh` ran `dotnet run` from source, which uses different config paths and behavior from the deployed binary. Led to config-not-found crashes and behavior differences.

**Solution:** Deleted the dev script. Updated the skill to use the installed launcher at `Mods/BOAM/start-tactical-engine.sh` which runs the deployed binary. Updated the launcher to open a gnome-terminal (user-visible) and tee output to `logs/tactical_engine.log`.

## Current Architecture

### Determinism Watchdog

Built into `Replay.fs`. Two modes set via `determinism` query param on `/replay/start` or `/navigate/replay/`:
- **`log`** — records all divergences, replay continues
- **`stop`** — halts replay at first divergence

On replay start, loads expected AI decisions from the same `round_log.jsonl`. As `/hook/action-decision` fires during replay, compares each against the expected sequence. Divergences record:
- Index in the AI decision sequence
- Round and actor
- Expected vs actual decision (behavior name, score, target tile)
- Last player action that preceded it

Endpoints:
- `GET /replay/divergences` — query divergences mid-replay
- `POST /replay/stop` — includes divergence list in response

### Select Command Flow (Fixed)

```
ExecuteSelect(actorUuid)
  → ActorRegistry.GetEntityId(uuid)
  → EntitySpawner.ListEntities(-1) → find by entityId → Il2CppUtils.GetManagedProxy()
  → TacticalState.GetUI().GetUnitsTurnBar().GetSlot(actorProxy)
  → slot.OnLeftClicked(slot)  // simulates portrait click
  → verify TacticalController.GetActiveActor() changed
```

### 4. RNG drift from the very first AI decision

**Problem:** Added per-agent Xorshift128 RNG state logging (`m_S0`-`m_S3` via `Agent.GetRandom()`) to AI decisions. Replay shows RNG state differs from the original recording starting at AI decision #0, round 1.

**Key finding:** The per-agent RNG is seeded differently between original play and replay. The game's `PseudoRandom` (Xorshift128) state is never the same.

**RNG Architecture (from extracted scripts):**
- `PseudoRandom` — Xorshift128, 4x uint32 state, seeded via `Init(int _seed)`
- `Agent.m_Random` — per-agent RNG instance
- `TacticalManager.s_Random` — global static RNG
- `Mission.m_Seed` → feeds `TacticalManager.OnInit()` → seeds everything
- Implements `ISaveStateProcessor` — state is serializable

**Why decisions still match early:** AI behavior scoring is deterministic (score-based comparison). RNG is used for tie-breaking, hit/miss resolution, damage rolls, etc. Early decisions are clear-cut (high score wins), so the different RNG doesn't matter. Once an RNG-dependent operation occurs (attack hit/miss), the game state diverges and subsequent decisions differ.

**Duplicate decision logging confirmed:** The `Agent.Execute` Prefix hook fires multiple times for the same evaluation — the expected RNG value stays constant across 14 consecutive entries for the same stinger, confirming re-evaluation spam rather than separate actions.

### 5. Agent.Execute fires multiple times per evaluation

**Problem:** `Patch_AgentExecute` (Prefix on `Agent.Execute`) logs every call, but the game calls `Execute()` in a loop (up to `MAX_ITERATIONS = 16`). Some of these are re-evaluations, not new actions — evidenced by identical RNG state and identical decisions logged consecutively.

**Status:** Not yet resolved. Need to distinguish "new action within turn" from "re-evaluation of same action."

## Current Status

**RNG is not deterministic across play/replay.** The root cause is that agent RNG seeding depends on something that differs between loading a save for play vs loading for replay. Possible causes:
- Global RNG (`TacticalManager.s_Random`) is consumed differently during mission setup (UI interactions, loading sequence differences)
- Agent construction order or timing differs
- The save state doesn't fully restore RNG state

**Decisions match for ~300 AI actions** because scoring is deterministic. Divergence occurs when RNG-dependent combat (hit/miss/damage) produces different outcomes.

### 6. Global RNG is deterministic — per-agent RNG is not

**Finding:** Captured `TacticalManager.s_Random` (Xorshift128 state) and `Mission.GetSeed()` at tactical-ready. Both are **identical** between recording and replay:
- Same mission seed: `8882706`
- Same global RNG state: `s0=1568221325 s1=1475707680 s2=2931975657 s3=2919300403`

But per-agent RNG (`Agent.m_Random`) differs from decision #0. Agent RNG is seeded during agent construction (before tactical-ready), likely derived from the global RNG. Something non-deterministic in the construction order/timing causes different per-agent seeds.

**Approach:** "Lazy restore" — capture per-agent RNG state on first `Agent.Execute` during recording, restore it on first `Agent.Execute` during replay.

### 7. Per-agent RNG restore achieves deterministic replay (SOLVED)

**Implementation:**
- `Patch_AgentExecute` tracks first Execute per agent via `_rngHandled` HashSet
- **Recording**: reads `m_S0`-`m_S3` via Il2Cpp property getters, sends to engine via `POST /hook/agent-rng`
- Engine appends each agent's RNG to `rng_states.json` in the battle report
- **Replay start**: engine loads `rng_states.json`, sends `agent_rng` array to bridge in the `/replay/start` notification
- Bridge parses states into `Dictionary<string, uint[]>`, passes to `Patch_AgentExecute.SetReplayRngStates()`
- **Replay**: on first `Agent.Execute` per agent, writes saved state back via `set_m_S0`-`set_m_S3`

**Result:** Pure-endturn battle (2 rounds, 25 agents, 631 AI decisions) replayed with **zero RNG drift and zero divergences**. Every AI decision matched the recording exactly.

**Files modified:**
- `src/AiObservationPatches.cs` — `Patch_AgentExecute`: RNG capture/restore logic
- `src/BoamCommandServer.cs` — `HandleReplayStart`: parse agent_rng, `HandleReplayStop`: clear states
- `boam_tactical_engine/Routes.fs` — `/hook/agent-rng` route, agent RNG in replay start payload
- `boam_tactical_engine/GameTypes.fs` — `RngState` type
- `boam_tactical_engine/HookPayload.fs` — RNG state parsing
- `boam_tactical_engine/ActionLog.fs` — RNG state serialization in AI decisions
- `boam_tactical_engine/Replay.fs` — RNG state in expected decisions, drift detection

### 8. Global RNG capture/restore per-agent Evaluate — still drifts

**Problem:** Added global RNG (`TacticalManager.s_Random`) capture/restore alongside per-agent RNG at each `Agent.Evaluate` call. Still drifts from decision #1. The game calls `Evaluate()` a variable number of times per agent (re-evaluation loop), and each call consumes RNG. We only capture at the first Evaluate (keyed by agent+round), so intermediate re-evaluations produce different RNG sequences.

**Key insight:** The game's AI loop is: `OnTurnStart → [Evaluate → PickBehavior → Execute] × N iterations`. `Evaluate()` consumes both per-agent and global RNG for tile scoring. The number of iterations and RNG consumption within each depends on the game state, which can differ subtly between play and replay even when player actions are identical (e.g., floating point accumulation, animation timing effects on state).

**Status:** Pure-endturn battles replay deterministically (no state changes between turns). Battles with player movement show RNG drift from very early on, causing AI divergence by round 2.

## RNG Restore Approach — Full Timeline and Why It Failed

### Attempt 1: Restore per-agent RNG once at first Execute
- Captured `Agent.m_Random` state on first `Agent.Execute` per agent
- Restored it on first `Agent.Execute` during replay
- **Result:** Pure-endturn battles: perfect (zero drift). With player actions: diverges in round 2.
- **Why:** RNG consumed by `Evaluate()` which runs before `Execute()`. By the time we restore at Execute, the RNG has already been used for tile scoring.

### Attempt 2: Move restore to Agent.Evaluate (before tile scoring)
- Moved capture/restore from `Patch_AgentExecute` to `Patch_AgentEvaluate` (Prefix on `Agent.Evaluate`)
- Capture/restore per (agent, round) on first Evaluate call
- **Result:** Still diverges. `OnTurnStart` fires per-actor (not per-faction as expected), so reset clears tracking correctly. But RNG drifts between agents within the same round because the global RNG is shared.

### Attempt 3: Also restore global RNG per faction turn
- Added `TacticalManager.s_Random` capture/restore alongside per-agent RNG
- Keyed by `(faction, round)`, restored once per faction turn start
- **Result:** Still diverges. Global RNG is consumed between agents within the same faction's turn. Restoring once per faction isn't enough.

### Attempt 4: Restore global RNG per-agent (before every Evaluate)
- Changed global RNG capture to key by `(actor, round)` — one snapshot per agent
- Both global and per-agent RNG restored before each agent's first Evaluate
- **Result:** Still 4 drifts from decision #1 in round 1. Divergence at #311-316 in round 2.
- **Why:** `Agent.Evaluate()` is called multiple times per agent in the AI loop (`MAX_ITERATIONS = 16`). We capture at the first call, but subsequent Evaluate calls consume both global and per-agent RNG differently because game state has subtly diverged.

### Root Cause Analysis

The game's AI processing loop per agent is:
```
OnTurnStart(actor)
  repeat (up to MAX_ITERATIONS=16):
    Evaluate()     ← consumes per-agent RNG for tile scoring
                   ← consumes global RNG for various systems
    PickBehavior() ← may consume RNG for tie-breaking
    Execute()      ← executes chosen behavior
    if behavior == Idle: break
```

Each `Evaluate()` call consumes RNG proportional to the number of tiles scored, which depends on the game state (unit positions, visibility, etc.). Even with identical player actions, the game state can differ between play and replay due to:
- **Floating point non-determinism**: tile score calculations involve floating point math that may differ due to CPU state, JIT compilation differences between sessions
- **Global RNG interleaving**: `TacticalManager.s_Random` is shared across all systems — combat resolution, pathfinding, animations, etc. Any system consuming it between our capture point and the decision changes the sequence
- **Re-evaluation count**: the number of Evaluate→Execute iterations per agent can vary if intermediate decisions produce slightly different outcomes

### Conclusion: RNG Restoration Is Not Viable

Restoring RNG state is fundamentally insufficient because:
1. The game has multiple RNG instances (per-agent + global) that interact unpredictably
2. The number of RNG calls between capture points varies based on game state
3. Game state itself is not perfectly reproducible across sessions (floating point, timing)

**What worked:** Pure-endturn battles (no player state changes → no game state divergence → same RNG consumption → perfect replay). This proves the mechanism works in theory but breaks under real conditions.

## New Approach: Decision Forcing

Instead of fighting the RNG, we record the actual AI decisions and **force** them during replay. Same principle as player actions — record what happened, replay it exactly.

**How it works:**
1. **Recording**: already captured — `round_log.jsonl` has every AI decision (behavior ID, name, target tile, score)
2. **Replay**: intercept `Agent.Execute`, look up the next expected decision for this actor, force `m_ActiveBehavior` to the matching behavior, set the target tile, let the game execute it
3. The AI's RNG becomes irrelevant because we override its choice

**Advantages:**
- No RNG capture/restore complexity
- Works regardless of floating point differences or re-evaluation counts
- Same approach as player action replay — consistent architecture
- The determinism watchdog still works for detecting when forced decisions differ from what the AI would have chosen (useful for AI modification impact analysis)

## Current Status — Decision Forcing + Combat Outcome Forcing (IMPLEMENTED)

RNG capture/restore abandoned. Replaced with two forcing systems that produce deterministic replay.

### Log Separation

AI decisions moved from `round_log.jsonl` to `ai_decisions.jsonl`. The round log now contains only actions and combat outcomes:
- **Player actions**: `player_click`, `player_useskill`, `player_endturn`, `player_select`
- **AI actions**: `ai_move`, `ai_useskill`, `ai_endturn` — patched via `TacticalManager.InvokeOnMovementFinished`, `InvokeOnSkillUse`, `InvokeOnTurnEnd`, filtered to non-player factions
- **Element hits**: `element_hit` — per-projectile, per-model damage via `Element.OnHit`

### AI Decision Forcing

**File**: `src/ReplayForcing.cs`, `src/AiObservationPatches.cs` (Patch_AgentExecute)

On replay start, the bridge fetches all expected AI decisions from the engine via `GET /replay/forcing-data`. At each `Agent.Execute` Prefix:
1. Peek at the next expected decision in the queue
2. If it matches the current actor, find the behavior by ID in `GetBehaviors()`
3. Set `agent.m_ActiveBehavior` to the matched behavior
4. For Move: set `moveBehavior.m_TargetTile` from `agent.m_Tiles` lookup
5. For SkillBehavior/Attack: set `skillBehavior.m_TargetTile` via `TileMap.GetTile(x, z)`

This forces every AI actor to execute the recorded behavior with the correct target.

### Combat Outcome Forcing (Element-Level)

**Problem**: Even with decision forcing, RNG variance in hit/miss rolls and element selection produces different combat outcomes. A unit surviving that should have died cascades into completely different game state.

**Solution**: Two-phase per-burst element hit forcing.

**Phase 1 — Zero wrong-element damage** (`Element.OnHit` Prefix in `AiActionPatches.cs`):
- On `InvokeOnAttackTileStart`, preload the expected element hits for the current burst (attacker→target pair) from the queue into a lookup by element index
- On each `Element.OnHit`, override `_damageAppliedToElement` via `ref` parameter:
  - If this element index was hit in recording → use recorded damage
  - If not → set to 0 (nullify the hit)
- This prevents wrong elements from taking damage or dying

**Phase 2 — Apply missed hits** (`ApplyMissedElementHits` in `ReplayForcing.cs`, called from `InvokeOnAfterSkillUse`):
- After each skill use completes, check if any recorded element hits were never consumed (game's RNG picked different elements or missed entirely)
- For each missed element: directly call `element.SetHitpoints(currentHp - recordedDamage)`
- This ensures elements that should have been hit take the correct damage

**Data flow**:
```
Recording:
  Element.OnHit fires → logs element_hit to round_log.jsonl
    {target, attacker, elementIndex, damage, elementHpAfter, elementAlive}

Replay start:
  Engine loads element_hits from round_log.jsonl
  Bridge fetches via GET /replay/forcing-data
  Parsed into Queue<ExpectedElementHit>

Replay:
  InvokeOnAttackTileStart → PreloadBurst(attacker, target)
    → Consume queue entries into _currentBurstDamageByElement[elementIndex] = damage
    → Copy to _missedElements (tracks which elements game hasn't hit yet)

  Element.OnHit Prefix → GetForcedDamage(attacker, target, elementIndex)
    → If element in burst lookup: return recorded damage, remove from _missedElements
    → If not: return 0

  InvokeOnAfterSkillUse → ApplyMissedElementHits()
    → For each remaining _missedElements: SetHitpoints(hp - recordedDamage)
```

**Result**: Per-element HP matches the recording exactly. Deaths are a natural consequence of HP reaching 0 — no need to intercept `Die()` or track kills separately.

### Files Modified

| File | Change |
|------|--------|
| `src/AiActionPatches.cs` | AI action logging (move/useskill/endturn), Element.OnHit prefix+postfix |
| `src/AiObservationPatches.cs` | Decision forcing in Patch_AgentExecute Prefix |
| `src/ReplayForcing.cs` | Forcing state: decision queue, element hit queue, burst preloading, missed hit application |
| `src/BoamCommandServer.cs` | Fetch forcing data on replay start |
| `src/DiagnosticPatches.cs` | PreloadBurst on AttackTileStart, ApplyMissedElementHits on AfterSkillUse |
| `src/BoamBridge.cs` | Patch registration for all new hooks |
| `modpack.json` | Added AiActionPatches.cs, ReplayForcing.cs to sources |
| `boam_tactical_engine/Replay.fs` | ElementHit type, parser, loader; ExpectedAiDecision with BehaviorId |
| `boam_tactical_engine/GameTypes.fs` | ElementHitPayload, AiActionPayload types |
| `boam_tactical_engine/HookPayload.fs` | parseElementHit, parseAiAction parsers |
| `boam_tactical_engine/ActionLog.fs` | logAiAction, logElementHit; decisions to ai_decisions.jsonl |
| `boam_tactical_engine/Routes.fs` | /hook/ai-action, /hook/combat-outcome, /replay/forcing-data routes |

### 9. InflictDamage polling desync — interrupt timing divergence

**Battle**: `battle_2026_03_18_22_06`

**Symptom**: Replay diverges in round 2. After `civilian.worker.2` acts correctly, `wildlife.alien_01_small_spiderling.1` takes a turn instead of the expected `wildlife.alien_stinger.2`. From that point, the queue is permanently out of sync and the replay ends with only 57/224 AI actions replayed.

**Root cause analysis**:

The divergence is caused by a **civilian.worker.1 interrupt firing at the wrong time** during replay.

**Recording sequence (round 2)**:
```
stinger.3 → Shoot @(12,22) → hits worker.1[1] for 6dmg
stinger.3 → Shoot @(12,22) (second shot, no hit logged)
stinger.3 → Move @(7,22) → endturn
[player acts]
worker.2 → Move @(15,23) → endturn
stinger.2 → Shoot @(12,22) → hits worker.1[1] for 1dmg → worker.1 INTERRUPT endturn
stinger.2 → Shoot @(5,19) → hits player.rewa
stinger.2 → Move @(6,24) → endturn
```

**Replay sequence (round 2)**:
```
stinger.3 → FORCE InflictDamage ×263 (lines 125-396, ~5 seconds of polling)
  Line 192: element_hit stinger.3→worker.1[2] 0dmg (FORCE DMG: 6→0, wrong element index)
  Line 285: element_hit stinger.3→worker.1[2] 0dmg (FORCE DMG: 3→0)
  Line 332: *** civilian.worker.1 ai_endturn *** (INTERRUPT FIRES EARLY)
stinger.3 → Move @(7,22) → endturn  ✓
[player acts]
worker.2 → Move @(15,23) → endturn  ✓
spiderling.1 → Move @(12,21) → endturn  ✗ (should be stinger.2!)
[replay effectively ends]
```

**What happened**:

1. During the original battle, `Agent.Execute` for `stinger.3` produced 263 `InflictDamage` calls (projectile in-flight polling, ~16ms each). All 263 were recorded in `ai_decisions.jsonl`.

2. During replay, the same 263 `InflictDamage` entries are correctly forced from the queue (FORCE log confirms all 263 match). The queue actor-matching guard (`expected.Actor == actorUuid`) ensures only stinger.3's entries are consumed.

3. However, the 5-second InflictDamage polling window creates a timing window during which the game engine processes other actors' reactions. `civilian.worker.1` (sitting at (12,22), the target tile) receives an interrupt and its `Agent.Execute` fires at replay line 332.

4. `TryForceDecision` correctly refuses to force worker.1 (queue head is stinger.3), so worker.1 runs its own PickBehavior → Idle → ai_endturn. **The queue is not corrupted** — worker.1's early interrupt doesn't consume any queue entry.

5. But worker.1's interrupt changes the **game state**: the turn order manager considers worker.1's turn "complete" for this round. In the recording, worker.1's interrupt happened later (during stinger.2's attack). This timing difference causes the turn scheduling engine to select a different next actor after worker.2.

6. When `spiderling.1` fires `Agent.Execute`, the queue head is `stinger.2 InflictDamage`. Actor mismatch → TryForceDecision returns false → spiderling.1 runs unforced. The queue is now permanently stuck (waiting for stinger.2 which never acts), and all subsequent actors run unforced.

**Why worker.1 interrupts early**:
- The `InflictDamage` behavior polls via `Agent.Execute` every ~16ms. The game is running normally during this time — other actors' AI can fire.
- In the original play, the interrupt timing was different (worker.1 reacted during stinger.2's attack, not stinger.3's)
- This is a **non-deterministic timing dependency**: the exact moment an interrupt fires depends on frame timing, animation state, and internal game scheduling — none of which are captured in the recording.

**Element index mismatch (secondary finding)**:
- Recording: stinger.3 hits worker.1 element [1] for 6 damage
- Replay: stinger.3 hits worker.1 element [2] — FORCE DMG correctly overrides 6→0
- The FORCE MISSED mechanism then applies damage to the correct element
- This is working as designed (combat outcome forcing handles element index differences)

**Key structural problem**: The InflictDamage polling behavior creates a wide timing window (263 frames / ~5 seconds) during which other actors' interrupts can fire. The queue forcing system handles the queue correctly (no corruption), but the **game's internal turn order** diverges because interrupts fire at different relative times.

### 10. Actor turn order forcing — failed approaches

**Goal**: Force the game to pick AI actors in the same order as the recording.

**Data**: Turn order derived from `ai_endturn` entries in `round_log.jsonl` — gives the exact sequence of actors per faction per round.

**Attempt 1: Patch `AIFaction.Pick(Actor)` Prefix with `ref Actor _actor`**
- Patch registered successfully (method found, Harmony confirmed)
- **Never fired at runtime** — Pick() is inlined by Il2Cpp into Process()
- Il2Cpp's ahead-of-time compilation eliminates the method call boundary

**Attempt 2: Patch `BaseFaction.set_m_ActiveActor(Actor)` Prefix**
- Failed: "Method is a field accessor, it can't be patched" (Il2CppInterop error)
- Il2Cpp property setters that just set a field have no method body to patch

**Attempt 3: Patch `AIFaction.Execute()` Prefix**
- Would fire on every AI evaluation iteration (up to 16× per actor turn)
- Requires tracking first-call-per-actor to avoid consuming turn order entries multiple times
- Not implemented — abandoned in favor of score rigging

### 11. Actor turn order forcing — score rigging approach (IMPLEMENTED)

**Idea**: Instead of intercepting the pick, control its INPUT. The game picks the actor with the highest `Agent.GetScoreMultForPickingThisAgent()`. Override the score so the recorded actor wins.

**How the game's pick works**:
```
AIFaction.Process() [per frame]
  ├── Think() → Agent.Evaluate() × all agents  [scores tiles/behaviors]
  ├── Pick logic:
  │     ├── Agent.GetScoreMultForPickingThisAgent() × all agents  ← OUR POSTFIX
  │     ├── Select highest score
  │     ├── Pick(actor)  [INLINED — sets m_ActiveActor]
  │     └── Agent.OnPicked()  [INLINED]
  ├── Execute() → Agent.Execute()  ← OUR PREFIX (decision forcing)
  └── InvokeOnTurnEnd(actor)  ← OUR POSTFIX (consume turn order)
```

Two scoring systems exist:
- **Behavior scores** (from Think/Evaluate): "what should this agent do?" — Move(677), Attack(120), Idle(1)
- **Pick scores** (GetScoreMultForPickingThisAgent): "how urgently does this agent need to act?" — used by the faction to prioritize which agent goes next. May be a multiplier on behavior scores.

**Patches**:
- `Agent.GetScoreMultForPickingThisAgent` **Postfix**: peek the turn order queue for this faction.
  - If this agent is the recorded next actor → `__result = 99999` (guaranteed pick)
  - Otherwise → `__result = -1` (suppressed)
- Turn order consumption in `InvokeOnTurnEnd` **Postfix**: when an AI actor's turn ends, consume the queue entry so the next cycle boosts the next recorded actor.

**Why not OnPicked for consumption**: `Agent.OnPicked()` is inlined by Il2Cpp (patch registers but never fires). `InvokeOnTurnEnd` fires exactly once per actor turn and is not inlined.

**Data**: Turn order derived from `ai_endturn` entries in `round_log.jsonl` — one entry per actor turn with actor UUID, faction, round. Loaded by engine, served via `/replay/forcing-data`.

**Per-faction queues**: Factions are interleaved in the recording (civilian f=3, wildlife f=7, civilian f=3...). A single global queue fails because when wildlife ends its turn, the queue front has a civilian entry — faction mismatch. Fix: `_turnOrderByFaction` is a `Dictionary<int, Queue<string>>` keyed by faction index. Each faction consumes from its own queue independently.

**Consumption validation**: `OnTurnEnd` only consumes if the ending actor matches the queue head for that faction. Mismatches (interrupt actors ending out of order) are logged as warnings without consuming.

**Flow per actor turn**:
1. Game evaluates pick scores → our Postfix boosts recorded next actor
2. Game picks that actor (highest score)
3. Actor acts (Move, Attack, etc.) — decision forcing + element hit forcing apply
4. `InvokeOnTurnEnd` fires → we consume queue entry
5. Next pick cycle → queue now points to next recorded actor
