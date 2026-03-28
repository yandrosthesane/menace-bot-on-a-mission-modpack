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

### Resolved

| # | Issue | Resolution |
|---|-------|-----------|
| 1 | Dual position state (actorPosDict + StateStore) | Eliminated actorPosDict, StateStore is single source of truth (Phase 3) |
| 4 | StateStore not thread-safe | Dictionary → ConcurrentDictionary (Phase 3) |
| 5 | Wrong-faction actors in pack scoring | Faction field on ActorPosState, pack filters by same faction (Phase 3) |
| 6 | Fake FactionState on turn-end | lastFactionState stored at turn-start, carried forward (Phase 3) |
| 8 | Skill data shape mismatch (idealRange) | Added idealRange to ActorRegistry.BuildDramatisPersonae (Phase 4) |
| 9 | actorPosDict never cleared between battles | Resolved by Issue 1 — StateStore.ClearAll at battle-end (Phase 3) |
| 10 | Dead code — BehaviorOverridePatch | Deleted (Phase 3) |
| 11 | Dead code — ShapeTileModifier | Deleted from fsproj and disk (Phase 3) |
| 13 | Monolithic on-turn-end handler | Contact detection moved to C# SyncTransform, turn-end handler streamlined (Phase 4) |
| 14 | Duplicated movement data gathering | Eliminated — static data stored once at tactical-ready in ActorStaticData (Phase 4) |
| 15 | Test node inline in Program.fs | Deleted (Phase 3) |
| 2/3 | Business logic in tactical-ready handler | OnTacticalReady hook point + walker; roaming-init and pack-init nodes (Phase 5) |
| 7 | Initial modifiers skip pack + reposition | Resolved by 2/3 — pack-init runs at tactical-ready with 3x boost |
| 12 | Synchronous flush, one POST per actor | Single tile-modifier-batch POST replaces N individual calls (Phase 5) |

### Remaining

All issues resolved. See `docs/done/7_architecture-cleanup-and-behaviour.md` for full implementation details.

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
