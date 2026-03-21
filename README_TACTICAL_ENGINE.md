# Tactical Engine

Companion process that renders heatmaps and logs actions. Runs as an HTTP server on port 7660.

Running outside the game keeps the in-game bridge thin and easy to maintain — it only patches and forwards data. All heavy logic (rendering, action logging) lives in the engine, which can be updated, restarted, or used standalone without touching the game.

For technical details (modules, hook endpoints, auto-navigation internals), see [docs/README_TACTICAL_ENGINE.md](docs/README_TACTICAL_ENGINE.md).

## Command-Line Arguments

| Argument | Description |
|----------|-------------|
| (none) | Start HTTP server, wait passively |
| `--on-title <route>` | Execute an engine route when Title scene is detected (e.g., `/navigate/tactical`) |
| `--render <battle>` | Render heatmaps from a battle session and exit (no HTTP server) |
| `--pattern <glob>` | Filter render jobs (default: `*`). Used with `--render` |

## CLI Examples

All examples assume `cd /path/to/Menace/Mods/BOAM/`.

```bash
# Start server -- passive, no auto-action
./start-tactical-engine.sh

# Start server + auto-navigate to tactical when game connects
./start-tactical-engine.sh --on-title /navigate/tactical

# Render heatmaps and exit (no server, no game needed)
./TacticalEngine --render battle_2026_03_15_15_14
./TacticalEngine --render battle_2026_03_15_15_14 --pattern "r01_*_stinger_*"
```

## HTTP Examples

```bash
# Health check
curl -s http://127.0.0.1:7660/status

# Render heatmaps
curl -s -X POST http://127.0.0.1:7660/render/battle/battle_2026_03_15_15_14 -d '{}'

# Navigate to tactical (game must be on Title)
curl -s -X POST http://127.0.0.1:7660/navigate/tactical

# Shutdown
curl -s -X POST http://127.0.0.1:7660/shutdown
```

## Battle Reports

All outputs for a session:

```
battle_reports/battle_YYYY_MM_DD_HH_MM/
+-- mapbg.png                     Captured map background
+-- mapbg.info                    Tile dimensions (texW,texH,tilesX,tilesZ)
+-- mapdata.bin                   Binary tile data (heights + flags)
+-- dramatis_personae.json        All actors with stable UUIDs
+-- round_log.jsonl               Chronological action log
+-- actor_*.jsonl                 Per-actor action logs
+-- render_jobs/                  Self-contained render job JSONs
|   +-- r01_wildlife_alien_stinger_1.json
|   +-- ...
+-- heatmaps/                     Rendered heatmap PNGs (created on demand)
    +-- r01_wildlife_alien_stinger_1.png
    +-- ...
```
