claude --resume 6346f05a-78df-48d4-8f78-1fb9b81260d5
# BOAM — Bot On A Mission

AI behavior analysis and replay mod for Menace.
Intercepts AI decision-making at runtime, 
captures tactical data for offline heatmap rendering, 
provides a real-time in-game minimap overlay,
records full battle sessions (player actions + AI decisions) for replay with divergence detection.

## Features

| Feature | Description |
|---------|-------------|
| [Tactical Minimap](README_MINIMAP.md) | In-game IMGUI overlay showing unit positions on the captured map background |
| [Heatmap Renderer](README_HEATMAPS.md) | Offline heatmap generation from deferred render jobs — tile scores, decisions, movement |
| [Replay System](README_REPLAY.md) | Record and replay full battles — player actions replayed exactly, AI decisions compared via determinism watchdog |
| [Configuration](README_CONFIG.md) | Versioned JSON5 configs with user/mod-default two-tier system |

## Components

| Component | Location | Runtime | Description |
|-----------|----------|---------|-------------|
| [C# Bridge Plugin](README_BRIDGE_PLUGIN.md) | `src/` | In-game (MelonLoader/Wine) | Harmony patches, map capture, minimap overlay, action forwarding |
| [F# Tactical Engine](README_TACTICAL_ENGINE.md) | `boam_tactical_engine/` | Native (.NET 10, port 7660) | Render jobs, heatmap renderer, action logger, replay engine, determinism watchdog |
| [Icon Generator](README_ICON_GENERATOR.md) | `boam_asset_pipeline/` | CLI tool | Resizes game badge art into heatmap/minimap icons |

**First time?** Follow the [Installation Guide](README_INSTALL.md).

## Install Layout

```
Menace/
├── Mods/BOAM/
│   ├── dlls/BOAM.dll              C# bridge (compiled by ModpackLoader)
│   ├── modpack.json               Mod manifest
│   ├── configs/                   Mod default configs (reset on deploy)
│   │   ├── config.json5           Engine ports, rendering, heatmaps toggle
│   │   ├── tactical_map.json5     Minimap keybindings, visual defaults
│   │   ├── tactical_map_presets.json5  Display presets (sizes, styles, anchors)
│   │   └── icon-config.json5      Icon generation source mappings
│   ├── tactical_engine/           Engine binary + runtime
│   │   └── TacticalEngine(.exe)
│   ├── start-tactical-engine.sh   Launcher (opens terminal, logs to file)
│   ├── boam-icons(.exe)           Icon generator
│   ├── icons/                     Generated heatmap/minimap icons
│   │   ├── factions/
│   │   └── templates/
│   └── logs/                      Engine log (overwritten each run)
│       └── tactical_engine.log
└── UserData/BOAM/
    ├── configs/                   User configs (persistent, checked first)
    ├── badges/                    Source art for icon generation
    ├── factions/
    └── battle_reports/            Recorded battles (auto-created per session)
        └── battle_YYYY_MM_DD_HH_MM/
            ├── mapbg.png          Captured map background
            ├── mapbg.info         Tile dimensions
            ├── mapdata.bin        Binary tile data
            ├── dramatis_personae.json  Actor registry (UUIDs, templates, factions)
            ├── round_log.jsonl    Action log (player actions + AI decisions)
            ├── render_jobs/       Self-contained render job JSON files
            └── heatmaps/          Rendered heatmap PNGs
```

## Usage

### Start the Engine

**Linux:**
```bash
# Passive — engine starts, you control everything
./start-tactical-engine.sh

# Auto-navigate to tactical when game connects
./start-tactical-engine.sh --on-title /navigate/tactical

# Auto-navigate + start a replay
./start-tactical-engine.sh --on-title /navigate/replay/battle_2026_03_15_15_14

# Replay with free camera (no auto-follow on actor switch)
./start-tactical-engine.sh --on-title "/navigate/replay/battle_2026_03_15_15_14?camera=free"

# Replay with determinism watchdog — halt on first AI divergence
./start-tactical-engine.sh --on-title "/navigate/replay/battle_2026_03_15_15_14?determinism=stop"

# Replay with determinism watchdog — log all divergences, don't halt
./start-tactical-engine.sh --on-title "/navigate/replay/battle_2026_03_15_15_14?determinism=log"
```

**Windows:**
```bat
REM Passive
start-tactical-engine.bat

REM Auto-navigate to tactical
start-tactical-engine.bat --on-title /navigate/tactical

REM Auto-navigate + replay with determinism watchdog
start-tactical-engine.bat --on-title "/navigate/replay/battle_2026_03_15_15_14?determinism=stop"
```

Then launch the game normally through Steam. On Linux the engine opens in its own terminal window; on Windows it runs in the command prompt. Logs written to `Mods/BOAM/logs/tactical_engine.log`.

### In-Game Minimap

| Key | Action |
|-----|--------|
| `M` | Toggle minimap on/off |
| `L` | Cycle display presets (size/anchor) |

Additional keys (FoW, labels, etc.) can be enabled in `tactical_map.json5`. See [Tactical Minimap](README_MINIMAP.md).

### Render Heatmaps

After playing a round, render job data is flushed to disk. Render heatmaps on demand:

**Linux:**
```bash
./TacticalEngine --render battle_2026_03_15_15_14                              # all
./TacticalEngine --render battle_2026_03_15_15_14 --pattern "r01_*"            # round 1 only
./TacticalEngine --render battle_2026_03_15_15_14 --pattern "*_alien_stinger*" # one unit
```

**Windows:**
```bat
tactical_engine\TacticalEngine.exe --render battle_2026_03_15_15_14
tactical_engine\TacticalEngine.exe --render battle_2026_03_15_15_14 --pattern "r01_*"
```

**HTTP (any platform, while engine is running):**
```bash
curl -s -X POST http://127.0.0.1:7660/render/battle/battle_2026_03_15_15_14 -d '{}'
```

See [Heatmap Renderer](README_HEATMAPS.md).

### Replay a Battle

**Linux:**
```bash
./start-tactical-engine.sh --on-title /navigate/replay/battle_2026_03_15_15_14
./start-tactical-engine.sh --on-title "/navigate/replay/battle_2026_03_15_15_14?determinism=stop"
```

**Windows:**
```bat
start-tactical-engine.bat --on-title /navigate/replay/battle_2026_03_15_15_14
start-tactical-engine.bat --on-title "/navigate/replay/battle_2026_03_15_15_14?determinism=stop"
```

**HTTP (any platform):**
```bash
curl -s http://127.0.0.1:7660/replay/battles                          # list recordings
curl -s -X POST http://127.0.0.1:7660/replay/start \
  -d '{"battle":"battle_2026_03_15_15_14","camera":"follow","determinism":"stop"}'
curl -s http://127.0.0.1:7660/replay/divergences                      # check divergences
curl -s -X POST http://127.0.0.1:7660/replay/stop                     # stop + get report
```

The determinism watchdog compares AI decisions during replay against the original recording. Divergences report which actor made a different decision, what was expected vs actual, and the last player action before the divergence.

See [Replay Manual](README_REPLAY.md).

### Generate Icons

**Linux:**
```bash
./boam-icons --force
```

**Windows:**
```bat
boam-icons.exe --force
```

See [Icon Generator](README_ICON_GENERATOR.md).

## Documentation

- [Installation Guide](README_INSTALL.md) — Setup, asset extraction, icon generation, shell shortcuts
- [Tactical Minimap](README_MINIMAP.md) — In-game overlay controls, display presets, customization
- [Heatmap Renderer](README_HEATMAPS.md) — Render API, pattern matching, what each heatmap shows
- [Replay Manual](README_REPLAY.md) — Recording, playback, determinism watchdog
- [Configuration](README_CONFIG.md) — Two-tier config system, versioning, all config options
- [Bridge Plugin](README_BRIDGE_PLUGIN.md) — Harmony hooks, data flow, map capture
- [Tactical Engine](README_TACTICAL_ENGINE.md) — HTTP endpoints, CLI arguments, modules
- [Icon Generator](README_ICON_GENERATOR.md) — Config format, fallback chain, customization
