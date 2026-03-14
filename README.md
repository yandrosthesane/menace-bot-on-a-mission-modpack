# BOAM — Bot On A Mission

AI behavior analysis and visualization mod for Menace. Intercepts AI decision-making at runtime and produces tactical heatmaps showing how each AI unit evaluates tile positions. Records all player actions for deterministic mission replay.

## Components

| Component | Location | Runtime | Description |
|-----------|----------|---------|-------------|
| [C# Bridge Plugin](README_BRIDGE_PLUGIN.md) | `src/` | In-game (MelonLoader/Wine) | Harmony patches that capture AI and player actions, forwards to tactical engine |
| [F# Tactical Engine](README_TACTICAL_ENGINE.md) | `boam_tactical_engine/` | Native (.NET 10, port 7660) | Heatmap renderer, action logger, replay engine |
| [Icon Generator](README_ICON_GENERATOR.md) | `boam_asset_pipeline/` | CLI tool | Resizes game badge art into heatmap icons |
| [Replay System](README_REPLAY.md) | (tactical engine) | HTTP API | Records and replays player actions for deterministic testing |

## Downloads (Nexus)

```
BOAM-modpack-v1.0.0.zip                       Required — C# bridge plugin
BOAM-tactical-engine-v1.0.0-linux-x64.tar.gz  Linux tactical engine + icon generator
BOAM-tactical-engine-v1.0.0-win-x64.zip       Windows tactical engine + icon generator
```

## Install Layout

```
Menace/Mods/BOAM/
├── dlls/BOAM.dll              C# bridge (compiled by ModpackLoader)
├── modpack.json               Mod manifest
├── tactical_engine/           Tactical engine binary + dependencies
│   ├── TacticalEngine(.exe)
│   └── config.json            Heatmap render settings
├── start-tactical-engine.sh   Linux launcher
├── start-tactical-engine.bat  Windows launcher
├── boam-icons(.exe)           Icon generator (self-contained)
├── icon-config.json           Icon source mappings
├── icons/                     Generated heatmap icons
│   ├── factions/
│   └── templates/
└── battle_reports/            Recorded battles (auto-created)
```

## Repository Layout (Development)

```
BOAM-modpack/
├── src/                       C# bridge plugin
├── boam_tactical_engine/      F# tactical engine
├── boam_asset_pipeline/       Icon generation
├── launcher/                  Platform start scripts
├── release/                   Built release archives
├── docs/                      Component documentation
├── release.sh                 Builds release archives
├── deploy.sh                  Dev deploy to local game
├── README_REPLAY.md      Replay user guide
└── modpack.json               Modpack manifest
```

## Quick Start

### 1. Start the tactical engine

Must be running before the game reaches the title screen.

```bash
# Linux
cd /path/to/Menace/Mods/BOAM/ && ./start-tactical-engine.sh

# Windows
cd C:\path\to\Menace\Mods\BOAM\ && start-tactical-engine.bat

# Verify
curl -s http://127.0.0.1:7660/status
```

### 2. Launch the game

Launch Menace normally through Steam. The bridge connects to the engine automatically. Check `MelonLoader/Latest.log` for:
```
[BOAM] Tactical engine found (status: ready)
```

### 3. Play a tactical mission

On each AI turn, the engine renders heatmaps and logs all decisions. Player actions are recorded automatically for replay.

### 4. View outputs

```
Mods/BOAM/battle_reports/battle_YYYYMMDD_HHMMSS/
├── combined_*.png         Heatmaps per AI actor
├── actor_*.jsonl          Per-actor action logs
└── round_log.jsonl        Chronological log (used by replay)
```

### 5. Replay a battle

See [Replay User Manual](README_REPLAY.md) for full instructions.

```bash
# List recorded battles
curl -s http://127.0.0.1:7660/replay/battles

# Replay all rounds
curl -s -X POST http://127.0.0.1:7660/replay/run \
  -d '{"battle":"battle_20260314_111300"}'
```

## Development Lifecycle

1. **Quit game** — DLLs are locked while running
2. **Deploy** — `deploy.sh` compiles and installs the bridge plugin
3. **Generate icons** — deploy wipes `icons/`, always regenerate after
4. **Start tactical engine** — must be up before game launch
5. **Launch game** — enter tactical to test

## Documentation

- [Bridge Plugin](README_BRIDGE_PLUGIN.md) — Harmony hooks, game bridge API, action logging
- [Tactical Engine](README_TACTICAL_ENGINE.md) — HTTP endpoints, heatmaps, battle reports
- [Icon Generator](README_ICON_GENERATOR.md) — Config format, fallback chain, customization
- [Replay Manual](README_REPLAY.md) — Recording, API, determinism, troubleshooting
