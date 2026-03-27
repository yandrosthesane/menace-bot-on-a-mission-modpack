# Architecture Cleanup — Audit & Plan

Audit of commit `2b7d047` (feat: prototype ok). The roaming + pack behaviours work, but the implementation grew incrementally and has structural issues.

## Guiding Principle: C# Transform Pipeline

The core architectural rule going forward:

```
Game hooks (Harmony patches)
    ↓ raw game objects
C# Transform pipeline (optional, registered transforms derive data)
    ↓ raw OR enriched payload (engine's choice)
F# Engine (receives decision-ready data, runs behaviour nodes)
    ↓ per-tile modifiers
C# Applicator (dumb lookup, no logic)
```

**Key rules:**
1. Every hook can be called **raw** — C# returns game state as-is, no transformation. The engine always has access to unprocessed data.
2. **Transforms are optional middleware** — registered separately, not baked into hooks. They derive values (cheapestAttack, maxDist, inContact) from raw game state.
3. **The engine decides** what it needs. It can request raw data and compute itself, or request pre-derived data. C# never decides what's relevant.
4. **C# does transforms because it has the game objects.** Things like AP budget math, contact detection, faction filtering — these read game state that only C# can access. The transforms execute C#-side but are driven by F# engine needs.
5. **Routes.fs is a thin routing layer** — parse, store, walk, flush. No business logic, no data derivation.

### Current Violations

- Routes.fs computes AP budget, maxDist, contact detection — these are transforms that should live C#-side
- Routes.fs tactical-ready handler inlines roaming computation — business logic that belongs in a node
- Hooks (AiActionPatches, ActorRegistry) mix raw data gathering with ad-hoc derivation
- No way to call a hook "raw" vs "enriched" — everything is hardcoded

### Target C# Structure

```
Hooks/          — Harmony patches, fire events, gather raw game state
Transforms/     — Optional data derivation (movement budget, contact, etc.)
Engine/         — Bridge to F# engine (TileModifierStore, CommandServer, EngineClient)
Applicators/    — Apply engine results (TileModifierPatch)
```

Each transform registers on a hook type. When the hook fires:
1. Raw data is gathered (always)
2. Registered transforms run, spreading derived fields into the payload (optional)
3. Payload is sent to the engine

---

## Issues

### High — Architectural

#### 1. Dual Position State
**Where:** Routes.fs — module-level `actorPosDict` (ConcurrentDictionary) + `actorPositions` StateStore key
**Problem:** Two sources of truth. The dict is "live", snapshotted to the store before each walker run. Stale data between snapshots. Never clears between battles.
**Fix:** Eliminate `actorPosDict`. Make StateStore thread-safe (Issue 4) and use it as the single source of truth.

#### 2. Business Logic in Routes.fs
**Where:** Routes.fs tactical-ready handler (~45 lines computing AP budget, maxDist, calling `computeTileModifiers`)
**Problem:** Roaming AP-budget math duplicated in Routes.fs and RoamingBehaviour.node.Run. Routes.fs is doing node work.
**Fix:** Two parts:
- Move AP-budget derivation to a C# transform (it reads game state)
- Move initial modifier computation to nodes via a `TacticalReady` hook point
- Routes.fs just parses, stores, walks

#### 3. Scalability — New Nodes Require Routes.fs Changes
**Where:** Routes.fs tactical-ready handler inlines node-specific init
**Problem:** Adding a new behaviour node with tactical-ready needs forces changes to Routes.fs.
**Fix:** `TacticalReady` hook point. Each node owns its init via `Reads`/`Writes` and runs through the walker. Routes.fs invokes the walker, nothing more.

#### 4. StateStore Not Thread-Safe
**Where:** StateStore.fs uses `Dictionary<string, obj>`
**Problem:** HTTP handlers dispatch concurrently. Plain Dictionary can corrupt. Root cause of the `actorPosDict` workaround.
**Fix:** Replace `Dictionary` with `ConcurrentDictionary` inside StateStore.

### Medium — Correctness

#### 5. Wrong-Faction Actors in Pack Scoring
**Where:** Routes.fs on-turn-end writes ALL actors' positions; pack node treats all entries as allies
**Problem:** Player units appear as pack allies for wildlife.
**Fix:** Include faction in `ActorPosState`. Pack node filters by same faction. Or: C# transform filters by faction before sending.

#### 6. Fake FactionState on Turn-End
**Where:** Routes.fs on-turn-end builds `FactionState` with `Opponents = []; Actors = []`
**Problem:** Any node accessing `ctx.Faction.Opponents` gets empty data silently.
**Fix:** Carry forward the last FactionState from turn-start (store it in a key), or make the walker not require FactionState when irrelevant.

#### 7. Initial Modifiers Skip Pack Scoring
**Where:** Routes.fs tactical-ready only calls RoamingBehaviour
**Problem:** First round has no pack influence. Behavioral discontinuity.
**Fix:** Resolved by Issue 3 — running the full walker at tactical-ready.

#### 8. Skill Data Shape Mismatch
**Where:** ActorRegistry.cs omits `idealRange`; AiActionPatches.cs includes it
**Problem:** `SkillInfo.IdealRange` parses as 0 from tactical-ready.
**Fix:** Shared C# skill-gathering helper. Or: C# transform produces consistent skill data for both paths.

#### 9. actorPosDict Never Cleared Between Battles
**Where:** Routes.fs module-level dict; battle-end handler doesn't clear it
**Problem:** Phantom actors from previous battles in pack scoring.
**Fix:** Resolved by Issue 1. StateStore clears via lifetime.

### Low — Code Hygiene

#### 10. Dead Code — BehaviorOverridePatch
**Where:** BehaviorOverridePatch.cs — empty Prefix, unreachable ForceIdle
**Fix:** Remove the file.

#### 11. Dead Code — ShapeTileModifier
**Where:** ShapeTileModifier.fs — commented out, still compiled
**Fix:** Remove from fsproj and delete.

#### 12. Synchronous .Result in Async Context
**Where:** Routes.fs flushTileModifiers — blocking HTTP calls
**Fix:** Make async, or batch into single POST.

#### 13. Monolithic on-turn-end Handler
**Where:** Routes.fs — ~80 lines of inline JSON parsing, contact detection, state management
**Fix:** Extract parsing to `HookPayload.parseOnTurnEnd`. Move contact detection to a C# transform.

#### 14. Duplicated Movement Data Gathering (C#)
**Where:** AiActionPatches.GatherMovementData + ActorRegistry.BuildDramatisPersonae
**Fix:** Shared helper or unified C# transform.

#### 15. Test Node Inline in Program.fs
**Where:** test-opponent-summary defined inline, always registered
**Fix:** Move to Nodes/ directory, gate behind config.

---

### 16. Route Explosion + Asymmetric Protocol
**Where:** Routes.fs — 15+ individual route registrations. C# CommandServer — 3 separate routes for tile modifiers.
**Problem:** Every game event is a separate HTTP route. The protocol is asymmetric — C# pushes to F# via hooks, F# pushes back via command routes. Adding a new event means a new route on both sides.
**Fix:** Symmetric protocol with two routes per side:
```
POST /query     ← ask for data (read-only, returns response)
POST /command   ← do something (side effects, returns ack)
```
Both C# and F# expose the same two endpoints. The `type` field in the payload identifies the operation:
```json
// C# → F# command (game event)
{"type": "hook", "hook": "turn-end", "actor": "...", ...}

// F# → C# command (apply modifiers)
{"type": "tile-modifier", "actor": "...", "tiles": [...]}

// F# → C# query (pull game data on demand)
{"type": "actor-skills", "actor": "wildlife.dragonfly.1"}

// C# → F# query (engine state)
{"type": "status"}
```
Both **push** and **pull** coexist in both directions:
- **Push** (command): something happened or should happen — C# pushes game events to F#, F# pushes modifiers to C#
- **Pull** (query): I need data — F# queries C# for actor skills, C# queries F# for engine status

The engine both reacts to pushed events AND pulls additional data when computing.

Benefits:
- New events = new dispatch case, no new route
- F# can pull data from C# on demand, not just receive pushes
- C# can push events without F# asking — both modes valid
- Symmetric: both sides speak the same protocol
- Enables the raw/enriched model: transforms register per `type`, engine chooses what it queries

---

## Implementation Order

### Phase 1: Symmetric Protocol — DONE

New files alongside existing code, zero risk.

- C#: `QueryCommandServer.cs` — POST /query + POST /command, dispatch by `{"type": "..."}`
- C#: `QueryCommandClient.cs` — client for C# → F# queries/commands
- F#: `Messaging.fs` — POST /query + POST /command handlers, dispatch by type
- F#: `MessagingClient.fs` — client for F# → C# commands (tile modifiers, ready signaling)

Also fixed during this phase:
- Engine check race condition — multiple `CheckEngine` threads from concurrent scene loads; added early-exit when `_engineAvailable` is already set
- Split `/query` "status" (heartbeat) from `/query` "features" (feature flags)
- Template AP fallback — `GetActionPointsAtTurnStart()` returns 0 for wildlife before their first turn; reads `EntityTemplate.Properties.ActionPoints` as fallback
- `boam-launch.sh` — added `pkill` fallback for killing stale engine processes

### Phase 2: Migrate & Cleanup — DONE

All traffic migrated to symmetric protocol, old code removed.

- All 15 hook routes migrated from `/hook/*` to `POST /command {"type":"hook","hook":"..."}`
- Tile modifier flush migrated to `MessagingClient.commandRaw`
- F# `HookHandlers.fs` dispatches all hooks via `Messaging`
- Removed from Routes.fs: all `/hook/*` routes, `flushTileModifiers`, `actorPosDict`, `currentRound`, mapping functions (570 → 140 lines)
- Removed C# `EngineClient.cs` (old sync client, zero callers)
- Removed C# `CommandServer.cs` (old `BoamCommandServer` HTTP listener); moved `Port` + `ActionCommand` to `BridgeServer` in `QueryCommandServer.cs`
- Routes.fs retained: `/status` GET, `/shutdown` POST, `/navigate/tactical`, `/render/battle/{name}`

### Phase 3: Foundation Cleanup — DONE

Data model fixes that eliminate workarounds.

- **StateStore thread safety (Issue 4)**: `Dictionary` → `ConcurrentDictionary`, `Remove` → `TryRemove`
- **Eliminated actorPosDict (Issues 1, 9)**: all position state flows through the thread-safe StateStore; `ClearAll()` at battle-end
- **Faction filtering (Issue 5)**: `ActorPosState` now carries `Faction: int`; pack node filters by `state.Faction = a.Faction` — player units no longer treated as wildlife allies
- **Proper FactionState on turn-end (Issue 6)**: stored at turn-start via `lastFactionState` key, carried forward to turn-end walker — nodes see real opponent data instead of empty lists
- **Dead code removal (Issues 10, 11, 15)**: removed `BehaviorOverridePatch.cs`, `ShapeTileModifier.fs`, inline test node from Program.fs

### Phase 4: C# Sync Transforms — DONE

C# owns data derivation from live game objects; F# owns domain decisions.

- **`src/Transforms/SyncTransforms.cs`**: static methods that enrich hook payloads with derived values
  - `ComputeContactState` — reads live vision + all entity positions, injects `inContact` bool
  - `ComputeMovementBudget` — reads live AP/skills/movement, injects `cheapestAttack`, `costPerTile`
- **Static data stored once**: skills and movement parsed at tactical-ready into `ActorStaticData` (per-session StateKey); turn-end no longer sends them in the payload
- **F# reads pre-computed values**: `ActorStatus` carries `CheapestAttack`, `CostPerTile` from C# transforms; `inContact` read from payload
- **Domain logic stays in F#**: `RoamingBehaviour` computes `maxDist = moveBudget / costPerTile` — node-specific decision, not a transform
- Removed `GatherMovementData` and skills gathering from C# turn-end path
- `modpack.json` updated: removed deleted files, added `SyncTransforms.cs`

### Phase 5: Performance — TODO

```
  └── Async flush / batch via single command (Issue 12)
```
