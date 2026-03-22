# Changes Since v1.3.0

Summary of work completed after the 1.3.0 release, for the next version.

## Tile Modifier System

Engine-controlled AI movement via tile score injection. The F# engine computes modifiers, sends them to the C# bridge, which applies them during AI tile evaluation.

### C# Side
- **TileModifierStore** (`src/Engine/TileModifierStore.cs`) — `ConcurrentDictionary<string, TileModifier>` keyed by actor UUID. Supports target mode (gradient toward tile) and distance gating mode. Ready signaling via `ManualResetEventSlim`.
- **TileModifierPatch** (`src/Hooks/TileModifierPatch.cs`) — `PostProcessTileScores` postfix. Applies utility bonus to tiles based on stored modifiers.
- **BehaviorOverridePatch** (`src/Hooks/BehaviorOverridePatch.cs`) — `Agent.Execute` prefix. Forces Idle on target arrival. Forces Idle when SuppressAttack is set and chosen behavior is attack/skill.
- **CommandServer routes** — `/tile-modifier`, `/tile-modifier/clear`, `/tile-modifier/ready`.
- **Ready signaling** — `SetPending()` at turn end, `WaitReady()` at AI turn start (event-driven, no timeout). Engine signals ready after flushing modifiers.

### F# Engine Side
- **OnTurnEnd hook point** — added to HookPoint union
- **TileModifier type** in `Domain/GameTypes.fs`
- **State keys** in `Nodes/Keys.fs` — `ai-actors` (PerSession), `tile-modifiers` (PerFaction)
- **ShapeTileModifier node** (`Nodes/ShapeTileModifier.fs`) — test node computing B-O-A-M letter shapes based on round. Reads `ai-actors`, writes `tile-modifiers`.
- **flushTileModifiers** in Routes.fs — reads modifiers from store, POSTs to bridge, signals ready
- **Initial seeding** — at tactical-ready, runs walker for round 1 so modifiers exist before first AI turn

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

## Files Added
- `src/Engine/TileModifierStore.cs`
- `src/Hooks/TileModifierPatch.cs`
- `src/Hooks/BehaviorOverridePatch.cs`
- `src/Boundary/ModpackConfig.cs`
- `configs/modpack.json5`
- `boam_tactical_engine/Nodes/Keys.fs`
- `boam_tactical_engine/Nodes/ShapeTileModifier.fs`

## Files Removed
- `src/Hooks/OpponentFilter.cs`
- `src/Hooks/VisibilityPatches.cs`
- `boam_asset_pipeline/generate-icons.sh`
