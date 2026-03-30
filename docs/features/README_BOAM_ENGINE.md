---
order: 7
---

# BOAM-engine

Companion process that runs outside the game. Runs AI behaviour nodes, receives hook data from the BOAM-modpack over HTTP, provides heatmap rendering, action logging, and icon generation.

The BOAM-engine is optional — the [BOAM-modpack](README_BOAM_MODPACK.md) works standalone for the minimap. Start the engine when you want AI behaviours, heatmaps, or action logging.

The engine binary lives in `UserData/BOAM/Engine/` and resolves all paths from the game directory (via `MENACE_GAME_DIR` env var or standard Steam install paths).

![BOAM-engine startup](/docs/images/tactical_engine_startup.png)

## Command-Line Arguments

| Argument | Description |
|----------|-------------|
| (none) | Start HTTP server, wait passively |
| `--on-title <route>` | Execute an engine route when Title scene is detected (e.g., `/navigate/tactical`) |
| `--render <battle>` | Render heatmaps from a battle session and exit (no HTTP server) |
| `--pattern <glob>` | Filter render jobs (default: `*`). Used with `--render` |

## CLI Examples

All examples assume `cd /path/to/Menace/Mods/BOAM/`.

<details>
<summary>Linux</summary>

```bash
# Start server -- passive, no auto-action
./start-tactical-engine.sh

# Start server + auto-navigate to tactical when game connects
./start-tactical-engine.sh --on-title /navigate/tactical

# Render heatmaps and exit (no server, no game needed)
../UserData/BOAM/Engine/TacticalEngine --render battle_2026_03_15_15_14
../UserData/BOAM/Engine/TacticalEngine --render battle_2026_03_15_15_14 --pattern "r01_*_stinger_*"
```

</details>

<details>
<summary>Windows</summary>

```bat
REM Start server -- passive
start-tactical-engine.bat

REM Start server + auto-navigate to tactical
start-tactical-engine.bat --on-title /navigate/tactical

REM Render heatmaps and exit
..\UserData\BOAM\Engine\TacticalEngine.exe --render battle_2026_03_15_15_14
..\UserData\BOAM\Engine\TacticalEngine.exe --render battle_2026_03_15_15_14 --pattern "r01_*_stinger_*"
```

</details>

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
