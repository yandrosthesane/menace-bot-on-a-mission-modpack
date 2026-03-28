# Heatmap Renderer -- Technical Reference

## Data Collection Flow

The `heatmaps` setting in `config.json5` controls whether render job data is collected.

During gameplay, three hooks contribute data:

1. **tile-scores hook** -- AI evaluation scores for each tile, plus all unit positions
2. **movement-finished hook** -- where the unit actually moved
3. **action-decision hook** -- which behavior the AI chose and what alternatives it considered

Data accumulates per actor per round in `RenderJobCollector`. When the next round starts (`on-turn-start`), the previous round's data is flushed to disk. At `battle-end`, any remaining data is flushed.

## Render Job JSON Structure

One JSON file per actor per round in `battle_reports/<session>/render_jobs/`:

```
render_jobs/
  r01_civilian_worker_1.json
  r01_wildlife_alien_stinger_1.json
  r02_wildlife_alien_stinger_1.json
  ...
```

Each file is self-contained -- everything needed to produce a heatmap without a running game:

| Field | Description |
|-------|-------------|
| `actor` | Stable UUID (e.g., `wildlife.alien_stinger.1`) |
| `round`, `faction` | Round number and faction index |
| `actorPosition` | Actor's tile position `{x, z}` |
| `tiles` | List of `{x, z, combined}` -- AI score per tile |
| `units` | All unit positions for the overlay |
| `visionRange` | Actor's vision range in tiles |
| `moveDestination` | Where the actor actually moved (null if didn't move) |
| `decision` | AI decision: chosen behavior, alternatives, attack candidates |
| `mapBgPath` | Path to the captured map background PNG |
| `mapInfoPath` | Path to the map info file (tile dimensions) |
| `iconBaseDir` | Path to the icon directory |

## RenderJobCollector

The collector accumulates data across multiple hooks for the same actor/round combination:

- **tile-scores** arrives first with tile data and unit positions
- **action-decision** attaches the AI's chosen behavior
- **movement-finished** attaches the actual move destination

On `on-turn-start`, all accumulated jobs for the previous round are serialized to JSON and written to `render_jobs/`. The collector then resets for the new round.

On `battle-end`, any jobs still in memory are flushed.

## Rendering Pipeline

When a render request arrives (`/render/battle/{name}` or `--render`):

1. Load render job JSON files matching the pattern
2. For each job:
   - Load the map background PNG and apply gamma correction
   - Parse `mapbg.info` for tile dimensions
   - Calculate output image size (upscaled if tiles are smaller than `minTilePixels`)
   - Draw tile scores as white text centered in each tile
   - Draw vision range borders (yellow) around tiles within manhattan distance
   - Draw special borders: actor position (red), best tile (green), move destination (blue)
   - Resolve and draw unit icons using the leader/template/faction fallback chain
   - Draw unit labels below icons
3. Save output PNG to `heatmaps/` directory

## Render API Response

```json
{
  "battle": "battle_2026_03_15_15_14",
  "pattern": "r01_wildlife_alien_stinger*",
  "rendered": 4,
  "errors": 0,
  "outputDir": ".../battle_2026_03_15_15_14/heatmaps",
  "results": [
    { "file": "r01_wildlife_alien_stinger_1.json", "status": "ok", "output": "r01_wildlife_alien_stinger_1.png" },
    { "file": "r01_wildlife_alien_stinger_2.json", "status": "ok", "output": "r01_wildlife_alien_stinger_2.png" }
  ]
}
```
