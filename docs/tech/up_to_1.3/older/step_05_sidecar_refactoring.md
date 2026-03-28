# Step 5: F# Sidecar Module Extraction

**Date:** 2026-03-13
**Status:** COMPLETE

## Overview

The F# sidecar grew feature-by-feature (opponent tracking, tile scores, heatmaps, icons, vision range, round counter). `Program.fs` was a 322-line monolith with inline JSON parsing, business logic, and HTTP routing. `HeatmapRenderer.fs` was 475 lines mixing pixel manipulation, domain logic, and the render pipeline.

Extracted focused modules to improve navigability and reuse.

## Modules Extracted

### FactionTheme.fs
Faction visual configuration — consolidated from HeatmapRenderer.fs:
- `factionColor` — RGBA per faction index
- `factionIconName` — faction → icon filename mapping
- `factionPrefix` — faction → label prefix (P, W, E, etc.)

### Naming.fs
Label and filename logic — extracted from both HeatmapRenderer.fs and Program.fs:
- `shortName` — template name → compact name (strip prefix, drop stopwords, keep last 2 segments)
- `templateFileName` — full name → icon filename (no stopword stripping)
- `unitDisplayName` — prefer leader nickname, fallback to shortName
- `buildUnitLabels` — assign per-faction indices, produce `W_stinger_1` style labels
- `makeHeatmapLabel` — combine shortName + actorId + round for filenames

`shortName` was duplicated in Program.fs (without stopword filtering) and HeatmapRenderer.fs (with stopwords). Unified to use the stopword-filtered version everywhere.

### HookPayload.fs
JSON parsing helpers + hook-specific payload parsers — extracted from Program.fs:
- `tryInt`, `tryStr`, `tryBool`, `tryArray` — reusable `TryGetProperty` wrappers (eliminates verbose match blocks)
- `parseOnTurnStart` — JSON → `FactionState`
- `parseTileScores` — JSON → `TileScoresPayload`
- `parseMovementFinished` — JSON → `MovementFinishedPayload`

### Rendering.fs
Low-level pixel primitives — extracted from HeatmapRenderer.fs:
- `MapInfo` type + `loadMapInfo`
- `prepareBackground` — load, upscale, gamma correct
- `tileOrigin` — tile coords → pixel coords (Y-flipped)
- `drawTileBorder` — unified from 3 near-identical functions (see Step 6)
- `drawRangeBorders` — outer-edge-only border for vision range overlay
- `resizeIcon`, `blitIcon` — icon scaling and alpha compositing

## New Types in GameTypes.fs

```fsharp
type TileScoreData = { X: int; Z: int; Combined: float32 }

type TileScoresPayload = {
    Round: int; Faction: FactionId; ActorId: int; ActorName: string
    ActorPosition: TilePos option; Tiles: TileScoreData list
    Units: UnitInfo list; VisionRange: int
}

type MovementFinishedPayload = { ActorId: int; Tile: TilePos }
```

`TileScoreData` moved from HeatmapRenderer.fs to GameTypes.fs (shared by HookPayload and HeatmapRenderer).

## Compile Order

```
Config.fs           ← config loader (added in Step 7)
GameTypes.fs        ← domain types + payload records
FactionTheme.fs     ← faction visuals
Naming.fs           ← label/filename logic
StateKey.fs         ← state key types
StateStore.fs       ← state persistence
NodeContext.fs       ← node execution context
Node.fs             ← node definitions
Registry.fs         ← node registration
Walker.fs           ← graph walker
Rendering.fs        ← pixel primitives
HookPayload.fs      ← JSON parsing
HeatmapRenderer.fs  ← render pipeline
Program.fs          ← HTTP server
```

## Result

| File | Before | After | Change |
|------|--------|-------|--------|
| HeatmapRenderer.fs | 475 lines | 211 lines | -56% |
| Program.fs | 322 lines | 227 lines | -30% |
| New modules | — | 319 lines | Focused, reusable |

## Files Changed

| File | Action |
|------|--------|
| `GameTypes.fs` | Added TileScoreData, TileScoresPayload, MovementFinishedPayload |
| `FactionTheme.fs` | **New** — faction colors, icons, prefixes |
| `Naming.fs` | **New** — label/filename logic |
| `HookPayload.fs` | **New** — JSON helpers + hook parsers |
| `Rendering.fs` | **New** — pixel primitives, MapInfo, borders |
| `HeatmapRenderer.fs` | Slimmed — uses extracted modules |
| `Program.fs` | Slimmed — uses HookPayload + Naming |
| `Sidecar.fsproj` | Added new files to compile order |
