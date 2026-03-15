# BOAM — Bot On A Mission

AI behavior analysis and visualization mod for Menace. Intercepts AI decision-making at runtime, captures tactical data for offline heatmap rendering, and provides a real-time in-game minimap overlay. Records all player actions for deterministic mission replay.

## Features

| Feature | Description |
|---------|-------------|
| [Tactical Minimap](README_MINIMAP.md) | In-game IMGUI overlay showing unit positions on the captured map background |
| [Heatmap Renderer](README_HEATMAPS.md) | Offline heatmap generation from deferred render jobs — tile scores, decisions, movement |
| [Replay System](README_REPLAY.md) | Record and replay player actions for deterministic testing |
| [Configuration](README_CONFIG.md) | Versioned JSON5 configs with user/mod-default two-tier system |

## Components

| Component | Location | Runtime | Description |
|-----------|----------|---------|-------------|
| [C# Bridge Plugin](README_BRIDGE_PLUGIN.md) | `src/` | In-game (MelonLoader/Wine) | Harmony patches, map capture, minimap overlay, action forwarding |
| [F# Tactical Engine](README_TACTICAL_ENGINE.md) | `boam_tactical_engine/` | Native (.NET 10, port 7660) | Render jobs, heatmap renderer, action logger, replay engine |
| [Icon Generator](README_ICON_GENERATOR.md) | `boam_asset_pipeline/` | CLI tool | Resizes game badge art into heatmap/minimap icons |

**First time?** Follow the [Installation Guide](README_INSTALL.md).

## Finalized Install Layout

Only mod related files are shown, there are also a lot of dependencies present.

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
│   ├── tactical_engine/           Engine binary + dependencies
│   │   └── TacticalEngine(.exe)
│   ├── boam-icons(.exe)           Icon generator
│   ├── icons/                     Generated heatmap/minimap icons
│   │   ├── factions/
│   │   └── templates/
│   └── battle_reports/            Recorded battles (auto-created per session)
│       └── battle_YYYY_MM_DD_HH_MM/
│           ├── mapbg.png          Captured map background
│           ├── mapbg.info         Tile dimensions
│           ├── mapdata.bin        Binary tile data
│           ├── dramatis_personae.json
│           ├── round_log.jsonl    Action log (used by replay)
│           ├── render_jobs/       Self-contained render job JSON files
│           └── heatmaps/          Rendered heatmap PNGs
└── UserData/BOAM/
    ├── configs/                   User configs (persistent, checked first)
    │   ├── tactical_map.json5
    │   └── tactical_map_presets.json5
    ├── badges/                    Source art for icon generation
    └── factions/
```

## Usage

All commands below assume you are in the BOAM mod directory:
```bash
cd /path/to/Menace/Mods/BOAM/
```

### Start the Engine

```bash
# Passive — engine starts, you control everything
./start-tactical-engine.sh

# Auto-navigate to tactical when game connects
./start-tactical-engine.sh --on-title /navigate/tactical

# Auto-navigate + start a replay when game connects (camera follows actor)
./start-tactical-engine.sh --on-title /navigate/replay/battle_2026_03_15_15_14

# Same but with free camera (no auto-follow on actor switch)
./start-tactical-engine.sh --on-title "/navigate/replay/battle_2026_03_15_15_14?camera=free"
```

Then launch the game normally through Steam.

### In-Game Minimap

| Key | Action |
|-----|--------|
| `M` | Toggle minimap on/off |
| `L` | Cycle display presets (size/anchor) |

Additional keys (FoW, labels, etc.) can be enabled in `tactical_map.json5`. See [Tactical Minimap](README_MINIMAP.md).

### Render Heatmaps

After playing a round, render job data is flushed to disk. Render heatmaps on demand:

```bash
# CLI — render and exit (no server needed)
./TacticalEngine --render battle_2026_03_15_15_14                              # all
./TacticalEngine --render battle_2026_03_15_15_14 --pattern "r01_*"            # round 1 only
./TacticalEngine --render battle_2026_03_15_15_14 --pattern "*_alien_stinger*" # one unit, all rounds

# HTTP — while engine is running
curl -s -X POST http://127.0.0.1:7660/render/battle/battle_2026_03_15_15_14 -d '{}'
curl -s -X POST http://127.0.0.1:7660/render/battle/battle_2026_03_15_15_14 \
  -d '{"pattern": "r01_*"}'
```

See [Heatmap Renderer](README_HEATMAPS.md).

### Replay a Battle

```bash
# CLI — fully automated: navigate to tactical + start replay
./start-tactical-engine.sh --on-title /navigate/replay/battle_2026_03_15_15_14
# Then launch the game — everything happens automatically

# HTTP — manual control while engine is running
curl -s http://127.0.0.1:7660/replay/battles
curl -s -X POST http://127.0.0.1:7660/replay/start \
  -d '{"battle":"battle_2026_03_15_15_14", "camera":"follow"}'
```

See [Replay Manual](README_REPLAY.md).

### Generate Icons

```bash
# Generate/regenerate all icons from source art
./boam-icons --force

# HTTP — check engine status
curl -s http://127.0.0.1:7660/status
```

See [Icon Generator](README_ICON_GENERATOR.md).

## Documentation

- [Installation Guide](README_INSTALL.md) — Step-by-step setup, asset extraction, icon generation
- [Tactical Minimap](README_MINIMAP.md) — In-game overlay controls, display presets, customization
- [Heatmap Renderer](README_HEATMAPS.md) — Render API, pattern matching, what each heatmap shows
- [Replay Manual](README_REPLAY.md) — Recording, playback API, determinism
- [Configuration](README_CONFIG.md) — Two-tier config system, versioning, all config options
- [Bridge Plugin](README_BRIDGE_PLUGIN.md) — Harmony hooks, data flow, map capture
- [Tactical Engine](README_TACTICAL_ENGINE.md) — HTTP endpoints, CLI arguments, modules
- [Icon Generator](README_ICON_GENERATOR.md) — Config format, fallback chain, customization
