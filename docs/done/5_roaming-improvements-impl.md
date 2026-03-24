# Roaming Improvements — Implementation Log

Started 2026-03-23.

## Architecture Change: Per-Tile Modifiers

Replaced the old single-modifier-per-actor system with per-tile utility maps. All scoring logic now lives in F#; C# is a pure applicator.

### Old System
- F# sent one `TileModifier` per actor (flat utility, min/max distance, target mode, suppress attack)
- C# `TileModifierPatch` contained gating logic, target gradient computation, distance checks
- Flat +100 utility to all tiles in range → no distance gradient → units moved only 1-3 tiles

### New System
- F# computes `Map<TilePos, float32>` per actor — individual utility per tile
- Utility scales with distance: `bonus = 100 * euclideanDistance`
- Gated by AP budget: `maxDist = (apStart - cheapestAttack) / lowestMovementCost`
- C# `TileModifierPatch` is ~5 lines: lookup `tileMap[(x,z)]`, add to score
- C# `TileModifierStore` holds `Dictionary<string, Dictionary<(int,int), float>>`

### Dataflow
```
Tactical-ready:
  C# BuildDramatisPersonae → includes position, apStart, skills, movementCosts per actor
  F# Routes.fs → parses, computes per-tile modifiers, flushes to bridge
  → Modifiers ready before round 1

Each turn-end:
  C# AiActionPatches.OnTurnEnd → sends actor status + movement costs
  F# RoamingBehaviour node → recomputes per-tile modifiers for that actor (new position)
  F# Routes.fs → flushes to bridge

AI evaluation:
  Game PostProcessTileScores → C# TileModifierPatch → lookup + add
```

## Test 1: CalculateTilesInMovementRange

**Result: Removed.** The async `.Result` call hangs the game — classic async deadlock on main thread. Returned 68 tiles for player before hanging. Not needed since movement costs are uniform per unit type.

## Test 2: Movement Cost Tables

**Result: Working.** All data flows correctly from C# to F#.

| Unit Type | AP Start | Cheapest Atk | Move Budget | Cost/Tile | Flying | Max Tiles |
|-----------|----------|-------------|-------------|-----------|--------|-----------|
| Dragonfly | 100 | 40 | 60 | 18 (all) | yes | 3 |
| Big warrior | 100 | 40 | 60 | 16 (all) | no | 3 |
| Spiderling | 120 | 40 | 80 | 16 (all) | no | 5 |
| Stinger | 100 | 40 | 60 | 16 (all) | no | 3 |
| Blaster bug | 100 | 40 | 60 | 16 (all) | no | 3 |
| Bombardier | 100 | 40 | 60 | 16 (all) | no | 3 |

Key finding: all terrain costs are uniform per unit type. No need for per-tile terrain data — `maxDist = budget / costPerTile` is exact.

## Changes Made

### F# Engine
- `Domain/GameTypes.fs` — Added `MovementData`, `TileModifierMap` types. Removed old `TileModifier` record.
- `Nodes/Keys.fs` — Store key changed from `Map<string, TileModifier>` to `Map<string, TileModifierMap>`
- `Nodes/RoamingBehaviour.fs` — Rewrote: `computeTileModifiers` generates per-tile utility scaled by distance. Node recomputes per actor at turn-end.
- `Nodes/ShapeTileModifier.fs` — Updated to use `TileModifierMap` (single-tile maps)
- `Routes.fs` — `flushTileModifiers` sends per-tile JSON. Tactical-ready computes initial modifiers from dramatis_personae.

### C# Bridge
- `Engine/TileModifierStore.cs` — Replaced `TileModifier` struct with `Dictionary<(int,int), float>` per actor. `SetFromJson` parses `{"actor":"...", "tiles":[{"x":1,"z":2,"u":150},...]}`.
- `Hooks/TileModifierPatch.cs` — Simplified to pure lookup: `tileMap[(x,z)]` → add to score.
- `Hooks/BehaviorOverridePatch.cs` — Gutted (target mode and suppress attack removed with old type). Kept as placeholder.
- `Hooks/AiActionPatches.cs` — Turn-end payload now uses `Dictionary<string,object>` with optional `GatherMovementData` spreader. Sends movement cost table per actor.
- `Tactical/ActorRegistry.cs` — `BuildDramatisPersonae` includes apStart, skills, and movement costs per actor.

## Decisions

- **CalculateTilesInMovementRange rejected** — async deadlock. Movement costs are uniform per unit type anyway.
- **Per-tile maps over modifier functions** — Simpler wire format, all logic in F#, C# is dumb. Future modifier composition can be done in F# before generating the tile map.
- **Payload dictionary pattern** — Turn-end payload uses `Dictionary<string,object>` so optional gatherers can spread fields. Extensible without touching the base payload.
- **Initial modifiers at tactical-ready** — Computed directly in Routes.fs from dramatis_personae data, not via walker. Ensures modifiers exist before round 1 without running the roaming node (which needs turnEndActor).
