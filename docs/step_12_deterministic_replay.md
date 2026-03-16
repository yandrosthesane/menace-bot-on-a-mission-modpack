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

## Current Status

Removing RNG capture/restore/logging code. Keeping:
- Determinism watchdog (compare decisions, halt/log modes, divergence endpoint)
- Select via OnLeftClicked (no RNG pollution)
- Per-agent RNG state in decision log entries (for diagnostic purposes only)

Next: implement decision forcing in `Patch_AgentExecute`.
