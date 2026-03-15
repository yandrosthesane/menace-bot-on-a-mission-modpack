# Heatmap Renderer

Renders heatmap PNG images showing AI evaluation scores overlaid on the captured map background. Each heatmap visualizes one actor's turn: tile scores, unit positions, vision range, chosen behavior, and move destination.

For technical details (data collection hooks, render job JSON structure, accumulation internals), see [docs/README_HEATMAPS.md](docs/README_HEATMAPS.md).

## What the Heatmap Shows

| Element | Visual | Description |
|---------|--------|-------------|
| Tile scores | White text per tile | Combined AI evaluation score |
| Unit icons | Leader/template/faction icon | All units on the map |
| Unit labels | Text below icon | Actor UUID |
| Vision range | Yellow border | Manhattan-distance vision tiles |
| Actor marker | Red border | The analyzed actor's position |
| Best tile | Green border | Highest-scored tile |
| Move destination | Blue border | Where the actor actually moved |

## CLI (renders and exits -- no server needed)

All examples assume `cd /path/to/Menace/Mods/BOAM/`.

```bash
# Render all jobs from a battle
./TacticalEngine --render battle_2026_03_15_15_14

# Render only stingers from round 1
./TacticalEngine --render battle_2026_03_15_15_14 --pattern "r01_*_stinger_*"

# Render all wildlife across rounds
./TacticalEngine --render battle_2026_03_15_15_14 --pattern "*_wildlife_*"

# Render a specific unit
./TacticalEngine --render battle_2026_03_15_15_14 --pattern "*_alien_big_blaster*"
```

## HTTP (while engine is running)

```bash
# Render all jobs
curl -s -X POST http://127.0.0.1:7660/render/battle/battle_2026_03_15_15_14 -d '{}'

# Render with pattern
curl -s -X POST http://127.0.0.1:7660/render/battle/battle_2026_03_15_15_14 \
  -d '{"pattern": "r01_wildlife*"}'
```

## Pattern Matching

The `pattern` field matches against render job filenames (without `.json`):

| Pattern | Matches |
|---------|---------|
| `"*"` (or omitted) | All jobs in the session |
| `"r01_*"` | Round 1 only |
| `"r02_*"` | Round 2 only |
| `"*_alien_stinger_*"` | All stinger units across rounds |
| `"r01_wildlife*"` | Round 1 wildlife units |
| `"*_player*"` | All player units (if AI-controlled) |

Output PNGs go to `battle_reports/<session>/heatmaps/`.

## Rendering Settings

The `rendering` section of `config.json5` controls heatmap appearance. The `heatmaps` toggle controls whether render job data is collected during gameplay.

| Setting | Default | Description |
|---------|---------|-------------|
| `minTilePixels` | 64 | Minimum pixels per tile (controls upscaling) |
| `gamma` | 0.35 | Gamma correction for map background (< 1.0 brightens) |
| `fontFamily` | `DejaVu Sans Mono` | Font for score text |
| `scoreFontScale` | 0.32 | Score text size relative to tile size |
| `labelFontScale` | 0.33 | Unit label size relative to tile size |
| `borders.actor` | Red | Border style for the analyzed actor |
| `borders.bestTile` | Green | Border style for the highest-scored tile |
| `borders.moveDest` | Blue | Border style for the move destination |
| `borders.vision` | Yellow | Vision range border color |
| `factionColors` | Per-faction | Unit icon/label colors keyed by faction index |

See [Configuration](README_CONFIG.md) for the full config reference.
