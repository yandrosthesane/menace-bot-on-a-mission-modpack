# Step 11: Deferred Render Jobs, Heatmap Renderer & TacticalMap Merge

**Status:** Implemented

## Problem

Heatmap generation blocked the AI evaluation thread (C# bridge) and the engine's HTTP handler (F# engine). The TacticalMap mod was a separate project duplicating map capture logic.

## Solution: Three Features

### A. Deferred Render Jobs

Instead of rendering heatmaps during gameplay, all per-actor tile-score data is accumulated in memory and flushed to self-contained render job JSON files at round boundaries. Rendering becomes a separate offline process — no running game needed.

### B. Heatmap Render Engine

A route on the tactical engine renders heatmap PNGs from render job files on demand, with glob pattern support for selective rendering.

### C. TacticalMap Minimap Overlay

The standalone TacticalMap mod was merged into BOAM as a togglable feature, sharing the same hook-driven data pipeline.

---

## A. Deferred Render Jobs

### Data Collection (during gameplay)

The `heatmaps` flag in `config.json5` controls whether render job data is collected.

- `tile-scores` hook → accumulates tile data, unit positions, actor position, vision range
- `movement-finished` hook → attaches move destination to the actor's data
- `action-decision` hook → attaches AI decision (chosen behavior, alternatives, attack candidates)
- `on-turn-start` hook → flushes the previous round's accumulated data to disk
- `battle-end` hook → flushes any remaining data (last round)

### Output

One JSON file per actor per round in `battle_reports/<session>/render_jobs/`:

```
battle_reports/battle_2026_03_15_15_14/
  mapbg.png              ← captured at mission prep
  mapbg.info             ← tile dimensions
  mapdata.bin            ← binary tile data (heights + flags)
  dramatis_personae.json ← all actors with UUIDs
  round_log.jsonl        ← action log
  render_jobs/
    r01_civilian_worker_1.json
    r01_wildlife_alien_stinger_1.json
    r01_wildlife_alien_stinger_2.json
    ...
  heatmaps/              ← rendered PNGs (created by render engine)
    r01_wildlife_alien_stinger_1.png
    ...
```

### Render Job Format

Each file is self-contained — everything needed to produce a heatmap PNG:

```json
{
  "round": 1,
  "faction": 7,
  "actor": "wildlife.alien_stinger.1",
  "actorPosition": { "x": 0, "z": 35 },
  "tiles": [{ "x": 0, "z": 30, "combined": -125.0 }, ...],
  "units": [{ "faction": 1, "x": 15, "z": 8, "actor": "player.carda", "name": "...", "leader": "carda" }, ...],
  "visionRange": 9,
  "moveDestination": { "x": 5, "z": 33 },
  "decision": {
    "chosen": { "behaviorId": 99999, "name": "Idle", "score": 1 },
    "alternatives": [...],
    "target": null,
    "attackCandidates": []
  },
  "mapBgPath": ".../battle_2026_03_15_15_14/mapbg.png",
  "mapInfoPath": ".../battle_2026_03_15_15_14/mapbg.info",
  "iconBaseDir": ".../Mods/BOAM/icons"
}
```

---

## B. Heatmap Render Engine

### Route

```
POST /render/battle/{battleName}
Content-Type: application/json

{ "pattern": "*" }
```

Renders heatmap PNGs from render job files in the specified battle session. Output goes to `battle_reports/<session>/heatmaps/`.

### Pattern Matching

The `pattern` field matches against render job filenames (without `.json` extension):

| Pattern | Matches |
|---------|---------|
| `"*"` (or omitted) | All jobs |
| `"r01_*"` | Round 1 only |
| `"r02_*"` | Round 2 only |
| `"*_alien_stinger_*"` | All stinger units across rounds |
| `"r01_wildlife*"` | Round 1 wildlife units |
| `"*_player*"` | All player units (if AI-controlled) |

### Response

```json
{
  "battle": "battle_2026_03_15_15_14",
  "pattern": "r01_wildlife_alien_stinger*",
  "rendered": 4,
  "errors": 0,
  "outputDir": ".../battle_2026_03_15_15_14/heatmaps",
  "results": [
    { "file": "r01_wildlife_alien_stinger_1.json", "status": "ok", "output": "r01_wildlife_alien_stinger_1.png" },
    ...
  ]
}
```

### What the Heatmap Shows

Each PNG renders onto the captured map background:
- **Tile scores** — combined score value per tile (white text)
- **Unit icons** — leader → template → faction fallback icon chain
- **Unit labels** — actor UUID below each icon
- **Vision range** — yellow border around the actor's vision tiles
- **Actor marker** — red border on the analyzed actor's tile
- **Best tile** — green border on the highest-scored tile
- **Move destination** — blue border on where the actor actually moved

---

## C. TacticalMap Minimap Overlay

### Overview

An in-game IMGUI minimap overlay showing unit positions on the captured map background. Merged from the standalone TacticalMap mod into BOAM.

### Data Source

Reads from `TacticalMapState` — a shared singleton updated by game hooks:
- Populated at tactical-ready with all actors (`BuildDramatisPersonae` + `EntitySpawner`)
- Updated by `tile-scores` (full unit refresh), `movement-finished` (position update), `actor-changed` (position update)
- No independent game polling

### Controls

| Key | Action | Default |
|-----|--------|---------|
| `M` | Toggle minimap on/off | Active |
| `L` | Cycle display presets (size/anchor combos) | Active |
| All others | Configurable in `tactical_map.json5` | Unset |

### Display Presets

Configured in `tactical_map_presets.json5`. Each preset combines:
- **MapStyle** — tile size (controls map scale)
- **EntityStyle** — icon size, font size, faction colors, panel chrome
- **Anchor** — screen position

Default presets: 1080 M, 1080 L, 4K M, 4K L.

### Icon Resolution

Same chain as heatmap renderer:
1. Leader icon: `icons/templates/{leader_name}.png`
2. Template icon: `icons/templates/{template_name}.png`
3. Faction icon: `icons/factions/{faction}.png`
4. Colored dot fallback

### Features

- Fog of War toggle (hide undetected enemies)
- Label toggle (show/hide unit names)
- Labels strip faction prefix, instance numbers, and noise words (`alien_`, `construct_`, `big`, `small`)
- Per-preset font size, icon size, opacity, brightness
- Performance: icon styles and labels cached per actor, unit snapshot only rebuilds on data change

---

## Config

All configs use JSON5 (comments supported) and have `configVersion` for forward compatibility.

User configs in `UserData/BOAM/configs/` take precedence over mod defaults in `Mods/BOAM/configs/`. If the user config version is older than the mod default, the mod default is used and a warning is logged.

| Config | Controls |
|--------|----------|
| `config.json5` | Engine ports, `heatmaps` toggle, rendering settings (borders, colors, gamma) |
| `tactical_map.json5` | Minimap keybindings, FoW/label defaults, opacity, layout |
| `tactical_map_presets.json5` | Display presets, map styles, entity styles, anchors |
| `icon-config.json5` | Icon generation pipeline sources and mappings |

---

## Files

| File | Role |
|------|------|
| `src/AiObservationPatches.cs` | Fire-and-forget tile-scores POST; update TacticalMapState |
| `src/PlayerActionPatches.cs` | Map capture at OnPreviewReady; battle session dir creation |
| `src/BoamBridge.cs` | Wire overlay lifecycle; initial unit population; map reload from disk |
| `src/TacticalMap/TacticalMapState.cs` | Shared singleton: map, units, battle session dir |
| `src/TacticalMap/TacticalMapOverlay.cs` | IMGUI minimap overlay with config versioning |
| `src/TacticalMap/TacticalMapTypes.cs` | Types: TileData, MapStyle, EntityStyle, OverlayUnit |
| `src/TacticalMap/MapGenerator.cs` | Tile/color-based map background generation |
| `src/TacticalMap/MapDataLoader.cs` | Load binary tile data from disk |
| `src/TacticalMap/ConfigLoader.cs` | Parse display presets JSON5 |
| `src/TacticalMap/JsonHelper.cs` | JSON5 mini-parser (strip comments) |
| `src/TacticalMap/ColorParser.cs` | Hex color parser |
| `boam_tactical_engine/RenderJobCollector.fs` | Accumulate + flush render job JSON files |
| `boam_tactical_engine/HeatmapRenderer.fs` | Render PNGs from tile data + map background |
| `boam_tactical_engine/Routes.fs` | Deferred accumulation + render route |
| `boam_tactical_engine/Config.fs` | Engine config with JSON5 support + versioned user config |
