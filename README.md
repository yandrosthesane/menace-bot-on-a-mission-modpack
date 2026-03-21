# BOAM — Bot On A Mission

AI behavior analysis mod for Menace.
Intercepts AI decision-making at runtime,
captures tactical data for offline heatmap rendering,
provides a real-time in-game minimap overlay,
and records full battle sessions (player actions + AI decisions + combat outcomes).

## Features

| Feature | Description |
|---------|-------------|
| [Tactical Minimap](docs/features/README_MINIMAP.md) | In-game IMGUI overlay showing unit positions on the captured map background |
| [Heatmap Renderer](docs/features/README_HEATMAPS.md) | Offline heatmap generation from deferred render jobs — tile scores, decisions, movement |
| [Action Logging](docs/features/README_CONFIG.md) | Records player actions, AI decisions, and combat outcomes to JSONL battle logs |
| [Configuration](docs/features/README_CONFIG.md) | Versioned JSON5 configs with user/mod-default two-tier system |

## Components

| Component | Location | Runtime | Description |
|-----------|----------|---------|-------------|
| [C# Bridge Plugin](docs/features/README_BRIDGE_PLUGIN.md) | `src/` | In-game (MelonLoader/Wine) | Harmony patches, map capture, minimap overlay, action forwarding |
| [F# Tactical Engine](docs/features/README_TACTICAL_ENGINE.md) | `boam_tactical_engine/` | Native (.NET 10, port 7660) | Render jobs, heatmap renderer, action logger |
| [Icon Generator](docs/features/README_ICON_GENERATOR.md) | `boam_asset_pipeline/` | CLI tool | Resizes game badge art into heatmap/minimap icons |

The minimap works standalone — no tactical engine needed. Start the engine only when you want heatmaps or action logging.

**First time?** Follow the [Installation Guide](docs/features/README_INSTALL.md).

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
```

**Windows:**
```bat
REM Passive
start-tactical-engine.bat

REM Auto-navigate to tactical
start-tactical-engine.bat --on-title /navigate/tactical
```

Then launch the game normally through Steam. On Linux the engine opens in its own terminal window; on Windows it runs in the command prompt. Logs written to `Mods/BOAM/logs/tactical_engine.log`.

### In-Game Minimap

| Key | Action |
|-----|--------|
| `M` | Toggle minimap on/off |
| `L` | Cycle display presets (size/anchor) |

Additional keys (FoW, labels, etc.) can be enabled in `tactical_map.json5`. See [Tactical Minimap](docs/features/README_MINIMAP.md).

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

See [Heatmap Renderer](docs/features/README_HEATMAPS.md).

### Generate Icons

**Linux:**
```bash
./boam-icons --force
```

**Windows:**
```bat
boam-icons.exe --force
```

See [Icon Generator](docs/features/README_ICON_GENERATOR.md).

## Documentation

- [Installation Guide](docs/features/README_INSTALL.md) — Setup, asset extraction, icon generation, shell shortcuts
- [Tactical Minimap](docs/features/README_MINIMAP.md) — In-game overlay controls, display presets, customization
- [Heatmap Renderer](docs/features/README_HEATMAPS.md) — Render API, pattern matching, what each heatmap shows
- [Configuration](docs/features/README_CONFIG.md) — Two-tier config system, versioning, all config options
- [Bridge Plugin](docs/features/README_BRIDGE_PLUGIN.md) — Harmony hooks, data flow, map capture
- [Tactical Engine](docs/features/README_TACTICAL_ENGINE.md) — HTTP endpoints, CLI arguments, modules
- [Icon Generator](docs/features/README_ICON_GENERATOR.md) — Config format, fallback chain, customization
