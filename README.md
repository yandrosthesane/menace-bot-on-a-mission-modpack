# BOAM — Bot On A Mission

**v2.0.3** | [Documentation](https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack) | [Changelog](docs/features/CHANGELOG.md)

AI behavior analysis and modification mod for Menace.
Modifies enemy AI movement through configurable behaviour nodes,
captures tactical data for offline heatmap rendering,
provides a real-time in-game minimap overlay,
and records full battle sessions (player actions + AI decisions + combat outcomes).

## Features

| Feature | Description |
|---------|-------------|
| [AI Behaviour](docs/features/README_BEHAVIOUR.md) | Configurable behaviour nodes that influence enemy AI movement |
| [Tactical Minimap](docs/features/README_MINIMAP.md) | In-game IMGUI overlay showing unit positions on the captured map background |
| [Heatmap Renderer](docs/features/README_HEATMAPS.md) | Offline heatmap generation from deferred render jobs — tile scores, decisions, movement |
| [Action Logging](docs/features/README_BOAM_ENGINE.md) | Records player actions, AI decisions, and combat outcomes to JSONL battle logs |
| [Configuration](docs/features/README_CONFIG.md) | Versioned JSON5 configs with user/mod-default two-tier system |

## Components

| Component | Location | Runtime | Description |
|-----------|----------|---------|-------------|
| [BOAM-modpack](docs/features/README_BOAM_MODPACK.md) | `src/` | In-game (MelonLoader/Wine) | Harmony patches, minimap overlay, map capture, action forwarding |
| [BOAM-engine](docs/features/README_BOAM_ENGINE.md) | `boam_tactical_engine/` | Native (.NET 10, port 7660) | Heatmap renderer, action logger, auto-navigation |
| [Icon Generator](docs/features/README_ICON_GENERATOR.md) | `boam_asset_pipeline/` | CLI tool | Resizes game badge art into heatmap/minimap icons |

The BOAM-modpack works standalone — the minimap needs no engine. Start the BOAM-engine only when you want heatmaps or action logging.

**First time?** Follow the [Installation Guide](docs/features/README_INSTALL.md).

## Downloads

Pre-built engine binaries (Linux and Windows) are available on the [Releases page](https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/releases/). This is the only distribution channel — binaries are not hosted elsewhere.

Each release includes:
- **BOAM-modpack** — C# source (compiled at deploy time by the Menace Modkit)
- **BOAM-tactical-engine** — pre-built binaries for Linux and Windows (bundled and slim variants)

Prefer to build yourself? See [Building from Source](docs/features/README_BUILD.md).

## Install Layout

```
Menace/
├── Mods/BOAM/
│   ├── dlls/BOAM.dll              C# bridge (compiled by ModpackLoader)
│   ├── modpack.json               Mod manifest
│   ├── configs/                   Mod default configs (reset on deploy)
│   │   ├── engine.json5           Engine ports, rendering, heatmaps toggle
│   │   ├── behaviour.json5       AI behaviour node chains and tuning presets
│   │   ├── tactical_map.json5     Minimap keybindings, visual defaults
│   │   ├── tactical_map_presets.json5  Display presets (sizes, styles, anchors)
│   │   └── icon-config.json5      Icon generation source mappings
│   ├── tactical_engine/           Engine binary + runtime
│   │   └── TacticalEngine(.exe)
│   ├── start-tactical-engine.sh   Launcher (opens terminal, logs to file)
│   ├── boam-icons(.exe)           Icon generator
│   ├── boam-launch.sh(.bat)       Steam launch helper
│   └── logs/                      Engine log (overwritten each run)
│       └── tactical_engine.log
└── UserData/BOAM/
    ├── configs/                   User configs (persistent, checked first)
    ├── icons/                     Generated heatmap/minimap icons
    │   ├── factions/
    │   └── templates/
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

<details>
<summary>Linux</summary>

```bash
# Passive — engine starts, you control everything
./start-tactical-engine.sh

# Auto-navigate to tactical when game connects
./start-tactical-engine.sh --on-title /navigate/tactical
```

</details>

<details>
<summary>Windows</summary>

```bat
REM Passive
start-tactical-engine.bat

REM Auto-navigate to tactical
start-tactical-engine.bat --on-title /navigate/tactical
```

</details>

Then launch the game normally through Steam. On Linux the engine opens in its own terminal window; on Windows it runs in the command prompt. Logs written to `Mods/BOAM/logs/tactical_engine.log`.

### In-Game Minimap

| Key | Action |
|-----|--------|
| `M` | Toggle minimap on/off |
| `L` | Cycle display presets (size/anchor) |

Additional keys (FoW, labels, etc.) can be enabled in `tactical_map.json5`. See [Tactical Minimap](docs/features/README_MINIMAP.md).

### Render Heatmaps

After playing a round, render job data is flushed to disk. Render heatmaps on demand:

<details>
<summary>Linux</summary>

```bash
./tactical_engine/TacticalEngine --render battle_2026_03_15_15_14                              # all
./tactical_engine/TacticalEngine --render battle_2026_03_15_15_14 --pattern "r01_*"            # round 1 only
./tactical_engine/TacticalEngine --render battle_2026_03_15_15_14 --pattern "*_alien_stinger*" # one unit
```

</details>

<details>
<summary>Windows</summary>

```bat
tactical_engine\TacticalEngine.exe --render battle_2026_03_15_15_14
tactical_engine\TacticalEngine.exe --render battle_2026_03_15_15_14 --pattern "r01_*"
tactical_engine\TacticalEngine.exe --render battle_2026_03_15_15_14 --pattern "*_alien_stinger*"
```

</details>

**HTTP (any platform, while engine is running):**
```bash
curl -s -X POST http://127.0.0.1:7660/render/battle/battle_2026_03_15_15_14 -d '{}'
```

See [Heatmap Renderer](docs/features/README_HEATMAPS.md).

### Generate Icons

<details>
<summary>Linux</summary>

```bash
./boam-icons --force
```

</details>

<details>
<summary>Windows</summary>

```bat
boam-icons.exe --force
```

</details>

See [Icon Generator](docs/features/README_ICON_GENERATOR.md).

## Documentation

- [Installation Guide](docs/features/README_INSTALL.md) — Setup, asset extraction, icon generation, shell shortcuts
- [AI Behaviour](docs/features/README_BEHAVIOUR.md) — How behaviour nodes work, configuration, adding custom nodes
- [Tactical Minimap](docs/features/README_MINIMAP.md) — In-game overlay controls, display presets, customization
- [Heatmap Renderer](docs/features/README_HEATMAPS.md) — Render API, pattern matching, what each heatmap shows
- [Configuration](docs/features/README_CONFIG.md) — Two-tier config system, versioning, all config options
- [BOAM-modpack](docs/features/README_BOAM_MODPACK.md) — In-game mod: minimap, hooks, map capture
- [BOAM-engine](docs/features/README_BOAM_ENGINE.md) — External engine: heatmaps, logging, CLI, HTTP API
- [Icon Generator](docs/features/README_ICON_GENERATOR.md) — Config format, fallback chain, customization
- [Building from Source](docs/features/README_BUILD.md) — Clone, build, and install from source
- [Changelog](docs/features/CHANGELOG.md) — Version history
