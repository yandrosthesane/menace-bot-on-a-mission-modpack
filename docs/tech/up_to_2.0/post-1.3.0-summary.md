# Changes Since v1.3.0

Summary of work completed after the 1.3.0 release, for the next version.

## Tile Modifier System

Engine-controlled AI movement via tile score injection. The F# engine computes modifiers, sends them to the C# bridge, which applies them during AI tile evaluation.

### C# Side
- **TileModifierStore** (`src/Engine/TileModifierStore.cs`) — `ConcurrentDictionary<string, Dictionary<(int,int), float>>` — per-tile utility maps keyed by actor UUID. Ready signaling via `ManualResetEventSlim`.
- **TileModifierPatch** (`src/Hooks/TileModifierPatch.cs`) — `PostProcessTileScores` postfix. Pure lookup: `tileMap[(x,z)]` → add to score. No scoring logic.
- **BehaviorOverridePatch** (`src/Hooks/BehaviorOverridePatch.cs`) — `Agent.Execute` prefix. Placeholder for future behavior overrides.
- **CommandServer routes** — `/tile-modifier` (per-tile JSON), `/tile-modifier/clear`, `/tile-modifier/ready`.
- **Ready signaling** — `SetPending()` at turn end, `WaitReady()` at AI turn start (event-driven, no timeout).
- **Turn-end payload** — uses `Dictionary<string,object>` with optional gatherers (e.g. `GatherMovementData`) that spread fields in.

### F# Engine Side
- **OnTurnEnd hook point** — added to HookPoint union
- **TileModifierMap type** in `Domain/GameTypes.fs` — `Map<TilePos, float32>` per actor
- **MovementData type** — per-surface AP costs, turning cost, lowest cost, flying flag
- **State keys** in `Nodes/Keys.fs` — `ai-actors` (PerSession), `tile-modifiers` (PerFaction, `Map<string, TileModifierMap>`)
- **RoamingBehaviour node** — computes per-tile utility: `bonus = 100 * distance`, gated by `maxDist = (apStart - cheapestAttack) / lowestMovementCost`
- **ShapeTileModifier node** (disabled) — test node for B-O-A-M letter shapes
- **flushTileModifiers** in Routes.fs — sends per-tile JSON per actor to bridge
- **Initial modifiers** — computed at tactical-ready from dramatis_personae (position, AP, skills, movement costs)

### Turn-End Hook
- **AiActionPatches.OnTurnEnd** consolidated (was split between DiagnosticPatches and AiActionPatches). Fires for all factions. POSTs `/hook/on-turn-end` to engine. Sets pending before POST.
- **Removed** `Patch_Diagnostics.OnTurnEnd` and its patch registration.
- **Engine** `/hook/on-turn-end` route runs walker for OnTurnEnd.Prefix, flushes modifiers.

### Dataflow Graph (Initial)
- Walker runs OnTurnEnd nodes in registration order
- Nodes read/write StateStore — pure computation, no I/O
- Route handler owns the I/O boundary (bridge communication)
- Validated: 2 nodes across 2 hooks (test-opponent-summary on OnTurnStart, shape-tile-modifier on OnTurnEnd)

## Modpack Config

- **New file**: `configs/modpack.json5` — independent C# bridge config
- **ModpackConfig.cs** (`src/Boundary/ModpackConfig.cs`) — loads with two-tier resolution (user → default)
- **opponent_filter** setting (experimental, currently unused — filtering reverted)

## Config Rename

- `config.json5` → `engine.json5` throughout codebase (F# engine, deploy script, all docs)

## Map Capture Fix

- **LaunchMission patch** — captures map data before scene transition, not only at OnPreviewReady
- **Cached preview** — OnPreviewReady caches instance + result, LaunchMission re-captures from cache
- **Toast** on map capture

## AI Investigation (Observation Only)

- Dragonfly observation log with 4 battle test cases (filter on/off, concealment on/off)
- Confirmed: AI evaluates concealed units via `Tile.IsVisibleToActor`, not just `m_Opponents`
- `m_Opponents` filtering ineffective — game rebuilds the list after OnTurnStart
- `IsVisibleToActor` patch works but was reverted — tile modifiers are the preferred approach
- Criterion comparison data exported to `docs/next/dragonfly-criterion-comparison.md`

### PackBehaviour Node
- Density-based pack scoring: attraction toward a few allies, repulsion from large clumps
- Ally influence = distance-weighted (1 - dist/radius), scaled by anchor weight (acted=1.0, unacted=0.3)
- Contact bonus: allies near opponents get +0.5 weight — pack converges toward threats
- Crowd curve: `attraction * min(density, peak) - crowdPenalty * max(0, density - peak)` (peak=2.5)
- Composes with roaming: adds pack scores to existing per-tile utility maps
- `ActorPosState` tracks position, HasActed, InContact per actor
- `knownOpponents` store key populated at turn-start, used for contact detection at turn-end

## Files Added
- `src/Engine/TileModifierStore.cs`
- `src/Hooks/TileModifierPatch.cs`
- `src/Hooks/BehaviorOverridePatch.cs`
- `src/Boundary/ModpackConfig.cs`
- `configs/modpack.json5`
- `boam_tactical_engine/Nodes/Keys.fs`
- `boam_tactical_engine/Nodes/ShapeTileModifier.fs`
- `boam_tactical_engine/Nodes/RoamingBehaviour.fs`
- `boam_tactical_engine/Nodes/PackBehaviour.fs`

## Dataflow Graph (Engine Side)

### OnTurnEnd Hook
- Added `OnTurnEnd` to `HookPoint` union
- Walker runs OnTurnEnd nodes from `/hook/on-turn-end` route
- Route handler parses actor status, writes to store, runs walker, flushes modifiers to bridge

### RoamingBehaviour Node
- Reads `turn-end-actor` + `tile-modifiers` from store
- Computes per-tile utility: `bonus = 100 * euclideanDistance`, gated by AP budget
- `maxDist = (apStart - cheapestAttack) / lowestMovementCost`
- Recomputes for each actor at turn-end (updated position)
- Initial modifiers computed at tactical-ready from dramatis_personae

### Turn-End Actor Status
- Full actor data sent from C# on every `InvokeOnTurnEnd`
- AP, HP, armor, vision, concealment, morale, suppression, stunned, dying, hasActed
- All attack skills with AP cost and range (min/max/ideal)
- Movement cost table: per-surface AP costs, turning cost, lowest cost, flying flag
- `ActorStatus`, `SkillInfo`, `MovementData` types in `Domain/GameTypes.fs`
- Turn-end payload uses `Dictionary<string,object>` with optional `GatherMovementData` spreader

### Consolidated Turn-End Hook
- `AiActionPatches.OnTurnEnd` handles all factions (player + AI)
- Removed duplicate `Patch_Diagnostics.OnTurnEnd`

## Files Removed
- `src/Hooks/OpponentFilter.cs`
- `src/Hooks/VisibilityPatches.cs`
- `boam_asset_pipeline/generate-icons.sh`
