# Step 11: Deferred Render Jobs & TacticalMap Merge

**Status:** Implemented

## Problem

Heatmap generation blocked the AI evaluation thread (C# bridge) and the engine's HTTP handler (F# engine). The TacticalMap mod was a separate project duplicating map capture logic.

## Solution: Deferred Render Jobs + Unified TacticalMap

### Architecture

Instead of rendering heatmaps during gameplay, all per-actor tile-score data is accumulated in memory and flushed to self-contained render job JSON files at round boundaries. Rendering becomes a separate offline process — no running game needed.

The TacticalMap in-game minimap overlay was merged into BOAM as a togglable feature, sharing the same hook-driven data pipeline.

### 1. C# Bridge: Fire-and-Forget POST

`Patch_PostProcessTileScores` fires the tile-scores POST via `ThreadPool.QueueUserWorkItem` — the AI evaluation thread is never blocked.

### 2. C# Bridge: Map Capture

`Patch_PreviewReady` captures the game's map texture, tile dimensions, and binary tile data (`mapbg.png`, `mapbg.info`, `mapdata.bin`) at mission prep. These are staged in `Mods/BOAM/` and copied into the battle session directory on battle-start.

### 3. C# Bridge: TacticalMap Overlay

The IMGUI minimap overlay reads unit positions from `TacticalMapState` — a shared singleton updated by game hooks (tile-scores, movement-finished, actor-changed). No independent game polling.

### 4. F# Engine: RenderJobCollector

The `RenderJobCollector` module accumulates per-actor per-round data:
- **tile-scores** → tile data, unit positions, actor position, vision range
- **movement-finished** → attach move destination
- **action-decision** → attach AI decision details

On round change (`on-turn-start`) or battle end, the collector flushes one JSON file per actor to `battle_reports/<session>/render_jobs/`:

```
render_jobs/
  r01_player.carda.json
  r01_wildlife.alien_stinger_1.json
  r02_player.carda.json
  ...
```

Each file is self-contained with all data needed for offline rendering: tiles, units, actor position, vision range, move destination, decision, and paths to map background and icons.

### 5. F# Engine: Routes

- `tile-scores` → accumulate in collector, return 200 immediately
- `movement-finished` → attach destination to collector
- `action-decision` → attach decision to collector
- `on-turn-start` → flush previous round's data
- `battle-end` → flush all remaining data

## Files Changed

| File | Change |
|------|--------|
| `src/AiObservationPatches.cs` | Fire-and-forget tile-scores POST; update TacticalMapState with unit positions |
| `src/PlayerActionPatches.cs` | Merge map capture into Patch_PreviewReady; update TacticalMapState from actor-changed |
| `src/BoamBridge.cs` | Wire TacticalMapOverlay lifecycle (OnGUI, OnUpdate, OnSceneLoaded) |
| `src/TacticalMap/TacticalMapState.cs` | Shared singleton: map texture, tile dims, unit positions |
| `src/TacticalMap/TacticalMapOverlay.cs` | IMGUI minimap overlay (merged from TacticalMap mod) |
| `src/TacticalMap/TacticalMapTypes.cs` | Types: TileData, MapStyle, EntityStyle, etc. |
| `src/TacticalMap/MapGenerator.cs` | Tile/color-based map background generation |
| `src/TacticalMap/MapDataLoader.cs` | Load binary tile data from disk |
| `src/TacticalMap/ConfigLoader.cs` | Parse display presets JSON5 |
| `src/TacticalMap/JsonHelper.cs` | JSON5 mini-parser (strip comments) |
| `src/TacticalMap/ColorParser.cs` | Hex color parser |
| `boam_tactical_engine/RenderJobCollector.fs` | Accumulate + flush render job JSON files |
| `boam_tactical_engine/Routes.fs` | Deferred accumulation instead of inline rendering |
| `boam_tactical_engine/Program.fs` | Remove TacticalMapFolder, HeatmapPaths from RouteContext |
| `boam_tactical_engine/TacticalEngine.fsproj` | Add RenderJobCollector.fs to compile order |
| `modpack.json` | Add TacticalMap sources + Unity module references |
| `configs/tactical_map.json5` | Minimap keybindings and visual defaults |
| `configs/tactical_map_presets.json5` | Display styles, map styles, entity styles, anchors |

## Impact

- Zero game performance impact — AI evaluation and hook processing are never blocked
- All render data persisted to disk — rendering can happen offline
- TacticalMap minimap overlay works in-game using the same hook-driven data
- Single BOAM mod replaces both BOAM + TacticalMap
