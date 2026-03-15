# Heatmap Renderer

Renders heatmap PNG images from deferred render jobs. During gameplay, tile-score data is accumulated in memory and flushed to self-contained JSON files at round boundaries. Rendering happens on demand via an HTTP API — no running game needed.

## How Data Gets Collected

The `heatmaps` setting in `config.json5` controls whether render job data is collected.

During gameplay:
1. **tile-scores hook** — AI evaluation scores for each tile, plus all unit positions
2. **movement-finished hook** — where the unit actually moved
3. **action-decision hook** — which behavior the AI chose and what alternatives it considered

Data accumulates per actor per round. When the next round starts (`on-turn-start`), the previous round's data is flushed to disk. At `battle-end`, any remaining data is flushed.

## Render Job Files

One JSON file per actor per round in `battle_reports/<session>/render_jobs/`:

```
render_jobs/
  r01_civilian_worker_1.json
  r01_wildlife_alien_stinger_1.json
  r02_wildlife_alien_stinger_1.json
  ...
```

Each file is self-contained — everything needed to produce a heatmap without a running game:

| Field | Description |
|-------|-------------|
| `actor` | Stable UUID (e.g., `wildlife.alien_stinger.1`) |
| `round`, `faction` | Round number and faction index |
| `actorPosition` | Actor's tile position `{x, z}` |
| `tiles` | List of `{x, z, combined}` — AI score per tile |
| `units` | All unit positions for the overlay |
| `visionRange` | Actor's vision range in tiles |
| `moveDestination` | Where the actor actually moved (null if didn't move) |
| `decision` | AI decision: chosen behavior, alternatives, attack candidates |
| `mapBgPath` | Path to the captured map background PNG |
| `mapInfoPath` | Path to the map info file (tile dimensions) |
| `iconBaseDir` | Path to the icon directory |

## Render API

```
POST /render/battle/{battleName}
Content-Type: application/json

{ "pattern": "*" }
```

### Pattern Matching

The `pattern` field matches against render job filenames (without `.json`):

| Pattern | Matches |
|---------|---------|
| `"*"` (or omitted) | All jobs in the session |
| `"r01_*"` | Round 1 only |
| `"r02_*"` | Round 2 only |
| `"*_alien_stinger_*"` | All stinger units across rounds |
| `"r01_wildlife*"` | Round 1 wildlife units |
| `"*_player*"` | All player units (if AI-controlled) |

### Response

```json
{
  "battle": "battle_20260315_151451",
  "pattern": "r01_wildlife_alien_stinger*",
  "rendered": 4,
  "errors": 0,
  "outputDir": ".../battle_20260315_151451/heatmaps",
  "results": [
    { "file": "r01_wildlife_alien_stinger_1.json", "status": "ok", "output": "r01_wildlife_alien_stinger_1.png" },
    { "file": "r01_wildlife_alien_stinger_2.json", "status": "ok", "output": "r01_wildlife_alien_stinger_2.png" }
  ]
}
```

Output PNGs go to `battle_reports/<session>/heatmaps/`.

## What the Heatmap Shows

Each PNG renders onto the captured map background:

| Element | Visual | Description |
|---------|--------|-------------|
| Tile scores | White text per tile | Combined AI evaluation score |
| Unit icons | Leader/template/faction icon | All units on the map |
| Unit labels | Text below icon | Actor UUID |
| Vision range | Yellow border | Manhattan-distance vision tiles |
| Actor marker | Red border | The analyzed actor's position |
| Best tile | Green border | Highest-scored tile |
| Move destination | Blue border | Where the actor actually moved |

## Commands

All examples assume `cd /path/to/Menace/Mods/BOAM/`.

### CLI (renders and exits — no server needed)

```bash
# Render all jobs from a battle
./TacticalEngine --render battle_20260315_151451

# Render only stingers from round 1
./TacticalEngine --render battle_20260315_151451 --pattern "r01_*_stinger_*"

# Render all wildlife across rounds
./TacticalEngine --render battle_20260315_151451 --pattern "*_wildlife_*"

# Render a specific unit
./TacticalEngine --render battle_20260315_151451 --pattern "*_alien_big_blaster*"
```

### HTTP (while engine is running)

```bash
# Render all jobs
curl -s -X POST http://127.0.0.1:7660/render/battle/battle_20260315_151451 -d '{}'

# Render with pattern
curl -s -X POST http://127.0.0.1:7660/render/battle/battle_20260315_151451 \
  -d '{"pattern": "r01_wildlife*"}'
```

## Rendering Settings

The `rendering` section of `config.json5` controls heatmap appearance:

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
