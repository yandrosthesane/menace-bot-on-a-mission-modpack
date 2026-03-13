# BOAM — Bot On A Mission

AI behavior analysis and visualization mod for Menace. Intercepts AI decision-making at runtime and produces tactical heatmaps showing how each AI unit evaluates tile positions.

## Architecture

BOAM is split into three components:

```
BOAM-modpack/
├── src/                      C# bridge plugin (compiled by modkit, runs in MelonLoader/Wine)
├── boam_tactical_engine/     F# graph engine + HTTP server (native Linux .NET 10)
├── boam_asset_pipeline/      Config-driven icon generation from game assets
├── docs/                     Design docs and implementation notes
└── modpack.json              Modpack manifest for the deploy tool
```

### src/ — C# Bridge Plugin

Thin MelonLoader plugin that runs inside the game under Wine/Proton. Hooks into the AI evaluation loop via Harmony patches and forwards data to the tactical engine over HTTP.

**Key hooks:**
- `AIFaction.OnTurnStart` — sends faction state (opponents, actors, round)
- `Agent.PostProcessTileScores` — sends per-tile AI scores + all unit positions

### boam_tactical_engine/ — F# Tactical Engine

Native Linux .NET 10 process that receives game hook data and renders heatmap visualizations. Runs as an HTTP server on port 7660.

**Key modules:**
- `HeatmapRenderer.fs` — composites map background + tile scores + unit icons into PNG overlays
- `GameTypes.fs` — shared type definitions (tiles, factions, units, scores)
- `Program.fs` — HTTP server, hook dispatch, payload parsing
- Graph engine (`Node.fs`, `Walker.fs`, `Registry.fs`) — behavior graph evaluation (WIP)

### boam_asset_pipeline/ — Icon Asset Pipeline

Generates properly sized icon assets from game badge art for use in heatmap unit overlays.

**Files:**
- `icon-config.json` — declares source→output mappings with named source directories
- `generate-icons.sh` — reads config, resizes PNGs via ffmpeg
- `IconGenerator.fs` — F# module for generating placeholder circle icons (manual invocation)

## Quick Start

### Prerequisites

- .NET 10 SDK (for the tactical engine)
- ffmpeg (for icon generation)
- Game installed with MelonLoader and ModpackLoader

### 1. Deploy the mod

```bash
/home/yandros/workspace/menace_mods/scripts/deploy-modpack.sh BOAM
```

This compiles the C# bridge plugin and installs it to the game's `Mods/BOAM/` directory.

### 2. Generate icons

**Linux:**
```bash
./boam_asset_pipeline/generate-icons.sh --force
```

**Windows:**
```cmd
boam-icons.exe --force --config boam_asset_pipeline\icon-config.json
```

The Windows exe (`boam-icons.exe`) is a self-contained single file — no .NET SDK or ffmpeg required. It has been tested under Wine but not on native Windows (I no longer own a Windows machine — if you hit issues, please open a bug). Download it from the releases or build it yourself:
```cmd
cd boam_asset_pipeline
dotnet publish -r win-x64 -c Release
:: Output: bin\Release\net10.0\win-x64\publish\boam-icons.exe
```

Must run **after** deploy — the deploy script wipes `Mods/BOAM/` and reinstalls from scratch. Icons are generated into `Mods/BOAM/icons/`.

### 3. Start the tactical engine

```bash
/home/yandros/workspace/menace_mods/scripts/start-sidecar.sh
```

Opens a terminal window running the F# engine on port 7660. Verify with:

```bash
curl -s http://127.0.0.1:7660/status
```

### 4. Launch the game

Launch via Steam (app ID 2432860). The bridge plugin connects to the engine automatically. Enter tactical mode to start generating heatmaps.

### 5. View heatmaps

Output location:
```
~/.steam/steam/steamapps/common/Menace/Mods/BOAM/heatmaps/
```

Filenames: `combined_{faction}_{template}_{actorId}.png`

## Icon System

### Fallback chain

When rendering a unit on the heatmap, the engine resolves its icon in this order:

1. **Leader** — `icons/templates/{leader_name}.png` (e.g., `rewa.png`, `exconde.png`)
2. **Template** — `icons/templates/{template_name}.png` (e.g., `alien_stinger.png`)
3. **Faction** — `icons/factions/{faction}.png` (e.g., `wildlife.png`)
4. **Colored square** — hard fallback if no icon found

When a leader or template icon is missing, the faction icon is auto-copied to the expected path so you have the correct filename ready to replace.

### Adding custom icons

1. Add source asset paths to `boam_asset_pipeline/icon-config.json`
2. Run `./boam_asset_pipeline/generate-icons.sh --force`
3. Restart the tactical engine (icon cache is in-memory)

### icon-config.json format

```json
{
  "defaults": {
    "size": 64,
    "output_base": "/path/to/Menace/Mods/BOAM/icons"
  },
  "sources": {
    "native": "/path/to/game/CustomPersistentAssets/BOAM",
    "custom": "/path/to/your/custom/icons"
  },
  "factions": [
    { "dir": "native", "source": "factions/enemy_faction_01.png", "output": "factions/wildlife.png" }
  ],
  "templates": [
    { "dir": "native", "source": "badges/squad_badge_bugs_stinger_234x234.png", "output": "templates/alien_stinger.png" }
  ],
  "leaders": [
    { "dir": "native", "source": "badges/leaders/rewa_badge_234x234.png", "output": "templates/rewa.png" }
  ]
}
```

Each entry supports an optional `"size"` override (default from `defaults.size`).

## Deployment Order

The correct lifecycle for testing changes:

1. **Quit game** — DLLs are locked while running
2. **Deploy** — `deploy-modpack.sh BOAM` (wipes and reinstalls `Mods/BOAM/`)
3. **Generate icons** — `generate-icons.sh --force` (repopulates `Mods/BOAM/icons/`)
4. **Start engine** — `start-sidecar.sh` (clears icon cache)
5. **Launch game** — enter tactical to test

**Important:** Deploy wipes the icons directory. Always regenerate icons after deploy.

## Heatmap Features

- **Gamma-corrected background** — tactical map brightened for readability
- **Tile scores** — compact numerical overlay showing AI evaluation per tile
- **Unit overlay** — all faction actors shown with badge icons
- **Leader labels** — player units labeled by character name (Rewa, Exconde, etc.)
- **Enemy labels** — units from other factions labeled with template short names
- **Best tile marker** — green border on the highest-scoring tile (intended target)
- **Actual destination** — blue border on where the unit actually stopped (AP-limited)
- **Per-actor output** — one heatmap per AI unit showing its specific evaluation
