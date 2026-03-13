# BOAM — Bot On A Mission

AI behavior analysis and visualization mod for Menace. Intercepts AI decision-making at runtime and produces tactical heatmaps showing how each AI unit evaluates tile positions.

## Architecture

BOAM is split into three components: a C# bridge plugin that runs inside the game, a F# tactical engine that runs as a standalone process, and an icon asset pipeline.

### Downloads (Nexus)

Three packages, one required and one platform-specific:

```
BOAM-modpack-v1.0.0.zip                       Required — C# bridge plugin
BOAM-tactical-engine-v1.0.0-linux-x64.tar.gz  Linux tactical engine + icon generator
BOAM-tactical-engine-v1.0.0-win-x64.zip       Windows tactical engine + icon generator
```

After installing both into `Menace/Mods/` (deploy the modpack as usual and extract your platform archive in the mod folder):

```
Menace/Mods/BOAM/
├── src/                          C# bridge source (compiled by ModpackLoader on game start, deploy like any other mod)
├── modpack.json                  Mod manifest
├── tactical_engine/              Tactical engine binary + dependencies
│   ├── TacticalEngine(.exe)
│   └── config.json
├── start-tactical-engine.sh|bat  Platform launcher script
├── boam-icons(.exe)              Icon generator (self-contained)
├── icon-config.json              Icon source mappings
└── README.md
```

### Repository (development)

```
BOAM-modpack/
├── src/                      C# bridge plugin (compiled by modkit, runs in MelonLoader)
├── boam_tactical_engine/     F# tactical engine + HTTP server (native .NET 10)
├── boam_asset_pipeline/      Icon generation from game badge art
├── launcher/                 Start scripts for Linux and Windows
├── release/                  Built release archives
├── docs/                     Design docs and implementation notes
├── release.sh                Builds all three release archives
├── deploy.sh                 Dev deploy to local game install
└── modpack.json              Modpack manifest
```

### src/ — C# Bridge Plugin

Thin MelonLoader plugin that runs inside the game under Wine/Proton. Hooks into the AI evaluation loop via Harmony patches and forwards data to the tactical engine over HTTP.

**Key hooks:**
- `AIFaction.OnTurnStart` — sends faction state (opponents, actors, round)
- `Agent.PostProcessTileScores` — sends per-tile AI scores + all unit positions
- `Agent.Execute` — captures AI behavior decisions (chosen action, alternatives, attack candidates)
- `TacticalManager.InvokeOnMovementFinished` — captures move destinations
- `TacticalManager.InvokeOnSkillUse` — captures player skill usage

### boam_tactical_engine/ — F# Tactical Engine

Native .NET 10 process that receives game hook data, renders heatmap visualizations, logs all actions, and supports mission replay. Runs as an HTTP server on port 7660.

**Key modules:**
- `HeatmapRenderer.fs` — composites map background + tile scores + unit icons into PNG overlays
- `ActionLog.fs` — per-actor and shared JSONL action logs in battle session directories
- `Replay.fs` — reads action logs and replays player actions through the game bridge
- `GameTypes.fs` — shared type definitions (tiles, factions, units, scores)
- `Program.fs` — HTTP server, hook dispatch, replay endpoints
- Graph engine (`Node.fs`, `Walker.fs`, `Registry.fs`) — behavior graph evaluation (WIP)

### boam_asset_pipeline/ — Icon Asset Pipeline

Generates properly sized icon assets from game badge art for use in heatmap unit overlays.

**Files:**
- `icon-config.json` — declares source→output mappings with named source directories
- `generate-icons.sh` — reads config, resizes PNGs via ffmpeg
- `IconGenerator.fs` — F# module for generating placeholder circle icons (manual invocation)

## Getting Started

### Install layout

After installation, your game's `Mods/BOAM/` directory should look like this:

```
Menace/Mods/BOAM/
├── tactical_engine/          Tactical engine binary + dependencies
│   ├── TacticalEngine        (Linux) or TacticalEngine.exe (Windows)
│   ├── config.json           Engine configuration
│   └── ...                   .NET runtime dependencies
├── dlls/                     Compiled bridge plugin
├── icons/                    Unit icons for heatmap overlays
│   ├── factions/
│   └── templates/
├── start-tactical-engine.sh  Linux start script
├── start-tactical-engine.bat Windows start script
├── modpack.json
└── README.md
```

### Step-by-step usage

#### 1. Start the tactical engine BEFORE launching the game

The tactical engine must be running before the game reaches the title screen. The bridge plugin checks for it on scene load — if it's not running, AI hooks will be disabled for that session.

**Linux:**
```bash
cd /path/to/Menace/Mods/BOAM/
./start-tactical-engine.sh
```

**Windows:**
Double-click `start-tactical-engine.bat`, or from a command prompt:
```cmd
cd C:\path\to\Menace\Mods\BOAM\
start-tactical-engine.bat
```

You should see a terminal window with:
```
  BOAM Tactical Engine v0.3.0
  Build: #3
  ─────────────────────────────────
  Port:    7660
  Target:  net10.0
  ─────────────────────────────────

  Listening on http://127.0.0.1:7660
  Waiting for game plugin...
```

To verify it's running:
```bash
curl -s http://127.0.0.1:7660/status
# Returns: {"engine":"BOAM Tactical Engine v0.3.0","status":"ready",...}
```

#### 2. Launch the game

Launch Menace normally through Steam. The bridge plugin connects to the tactical engine automatically on scene load.

In the game logs (`MelonLoader/Latest.log`), you should see:
```
[BOAM] Tactical engine found (status: ready)
```

If you see `[BOAM] Tactical engine not available`, the engine wasn't running when the game loaded — restart the engine and relaunch the game.

#### 3. Play a tactical mission

Enter any tactical mission. On each AI turn, the engine will:
- Receive tile score data from every AI unit
- Render heatmap PNGs showing how each unit evaluates positions
- Log all AI decisions and player actions to JSONL files

#### 4. View outputs

All outputs are in the battle session directory:
```
Mods/BOAM/battle_reports/battle_YYYYMMDD_HHMMSS/
├── combined_7_alien_stinger_15.png    Heatmap for actor 15 (faction 7)
├── combined_7_alien_stinger_16.png    Heatmap for actor 16
├── actor_7_15_alien_stinger.jsonl     Per-actor action log
├── actor_1_4_player_squad_carda.jsonl Per-actor action log (player)
└── round_log.jsonl                    Shared chronological log
```

Each heatmap shows:
- **Tile scores** — compact numbers showing AI evaluation per tile
- **Unit overlay** — all faction actors shown with badge icons and labels
- **Red border** — actor's current position
- **Green border** — best tile (highest combined score)
- **Blue border** — where the unit actually moved (may differ due to AP limits)

### Replay

BOAM records all player actions (moves and skill uses) during a battle. You can replay these actions on a fresh tactical mission to reproduce the exact same player behavior.

**List recorded battles:**
```bash
curl -s http://127.0.0.1:7660/replay/battles
```

**View actions from a battle:**
```bash
curl -s http://127.0.0.1:7660/replay/actions/battle_20260313_201138
```

**Replay a battle (all rounds):**
```bash
curl -s -X POST http://127.0.0.1:7660/replay/run \
  -H 'Content-Type: application/json' \
  -d '{"battle":"battle_20260313_201138","delayMs":2000}'
```

**Replay a specific round:**
```bash
curl -s -X POST http://127.0.0.1:7660/replay/run \
  -H 'Content-Type: application/json' \
  -d '{"battle":"battle_20260313_201138","round":1,"delayMs":2000}'
```

The game must be in tactical mode with the same save loaded. The engine sends `select` and `move`/`useskill` commands to the game bridge to reproduce each recorded action.

## Icon System

BOAM uses unit icons on heatmap overlays to identify actors at a glance. Icons are generated once during setup by resizing source art from the game assets (or your own PNGs) down to 64x64 tiles.

### Generating icons

Run the icon generator from the BOAM mod folder. It reads `icon-config.json` and resizes every source PNG to the configured output location.

**Linux:**
```bash
cd /path/to/Menace/Mods/BOAM/
./boam-icons
```

**Windows:**
```cmd
cd C:\path\to\Menace\Mods\BOAM\
boam-icons.exe
```

Output:
```
BOAM Icon Generator v1.0.0
  Config: ./icon-config.json
  Output: /path/to/Menace/Mods/BOAM/icons
  Default size: 64x64
  Entries: 51

  OK: factions/wildlife.png  (64x64)
  OK: factions/player.png  (64x64)
  OK: templates/alien_stinger.png  (64x64)
  OK: templates/rewa.png  (64x64)
  ...

Done: 51 generated, 0 skipped, 0 missing, 0 errors
```

**Options:**

| Flag | Description |
|------|-------------|
| `--force` | Overwrite existing icons (default: skip existing files) |
| `--config <path>` | Path to config file (default: `icon-config.json` next to the executable) |
| `--help` | Show usage |

By default, existing icons are preserved — only missing icons are generated. Use `--force` after updating source art or changing icon sizes:

```bash
./boam-icons --force
```

### When to generate

- **First install** — run once after extracting the tactical engine archive
- **After updating source art** — run with `--force` to regenerate from new sources
- **After adding new entries** — run normally (only new icons are generated)
- **After deploy (development)** — deploy wipes the icons directory, regenerate after every deploy

### Fallback chain

When rendering a unit on the heatmap, the engine resolves its icon in this order:

1. **Leader** — `icons/templates/{leader_name}.png` (e.g., `rewa.png`, `exconde.png`)
2. **Template** — `icons/templates/{template_name}.png` (e.g., `alien_stinger.png`)
3. **Faction** — `icons/factions/{faction}.png` (e.g., `wildlife.png`)
4. **Colored square** — hard fallback if no icon found

When a leader or template icon is missing, the engine auto-copies the faction icon to the expected path. This seeds placeholder files with the correct filenames — replace them with proper art whenever you want.

### icon-config.json

The config file declares where source art lives and how each icon maps from source to output.

#### Structure

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
  "factions": [ ... ],
  "templates": [ ... ],
  "leaders": [ ... ]
}
```

| Field | Description |
|-------|-------------|
| `defaults.size` | Default output size in pixels (width = height). All icons are square. |
| `defaults.output_base` | Root directory for generated icons. The engine expects `factions/` and `templates/` subdirectories here. |
| `sources` | Named source directories. Each entry in the icon lists references a source by its label. You can define as many as you need. |
| `factions` | Faction-level icons — one per faction. Output goes to `factions/`. |
| `templates` | Unit template icons — one per unit archetype. Output goes to `templates/`. |
| `leaders` | Leader icons — one per named character. Output also goes to `templates/` (leaders override templates in the fallback chain). |

#### Entry format

Each entry in `factions`, `templates`, and `leaders` has:

```json
{ "dir": "<source_label>", "source": "<relative_path>", "output": "<relative_output>", "size": 64 }
```

| Field | Required | Description |
|-------|----------|-------------|
| `dir` | yes | Label from `sources` — resolves to the base directory |
| `source` | yes | Path relative to the source directory |
| `output` | yes | Path relative to `output_base` |
| `size` | no | Override `defaults.size` for this entry |

#### Examples

**Faction icon** — resize a game badge to the wildlife faction icon:
```json
{ "dir": "native", "source": "factions/enemy_faction_01.png", "output": "factions/wildlife.png" }
```
Source: `<native>/factions/enemy_faction_01.png` → Output: `<output_base>/factions/wildlife.png` (64x64)

**Template icon** — resize a squad badge to a unit template icon:
```json
{ "dir": "native", "source": "badges/squad_badge_bugs_stinger_234x234.png", "output": "templates/alien_stinger.png" }
```
Source: `<native>/badges/squad_badge_bugs_stinger_234x234.png` (234x234) → Output: `<output_base>/templates/alien_stinger.png` (64x64)

**Leader icon** — resize a character portrait badge:
```json
{ "dir": "native", "source": "badges/leaders/rewa_badge_234x234.png", "output": "templates/rewa.png" }
```
Source: `<native>/badges/leaders/rewa_badge_234x234.png` → Output: `<output_base>/templates/rewa.png` (64x64)

**Custom icon from your own art** — add a `"custom"` source directory and reference it:
```json
"sources": {
  "native": "/path/to/game/CustomPersistentAssets/BOAM",
  "custom": "/home/user/my-boam-icons"
}
```
```json
{ "dir": "custom", "source": "my_alien_queen.png", "output": "templates/alien_queen.png" }
```
Source: `/home/user/my-boam-icons/my_alien_queen.png` → Output: `<output_base>/templates/alien_queen.png` (64x64)

**Larger icon** — override the default size for a specific entry:
```json
{ "dir": "native", "source": "badges/squad_badge_bugs_queen_234x234.png", "output": "templates/alien_queen.png", "size": 96 }
```
Output: 96x96 instead of 64x64

### Quick customization without config

You can also skip the generator entirely and place 64x64 PNGs directly into the icons directory:

```
Mods/BOAM/icons/
├── factions/
│   ├── wildlife.png        One per faction (neutral, player, wildlife, etc.)
│   └── constructs.png
└── templates/
    ├── alien_stinger.png   One per unit type
    ├── rewa.png            One per leader (overrides template)
    └── darby.png
```

The engine loads icons by filename — drop a PNG with the right name and restart the engine.

## Deployment Order (Development)

The correct lifecycle for testing changes during development:

1. **Quit game** — DLLs are locked while running
2. **Deploy** — compiles and installs the C# bridge plugin to `Mods/BOAM/`
3. **Generate icons** — `generate-icons.sh --force` (repopulates `Mods/BOAM/icons/`)
4. **Start tactical engine** — `start-tactical-engine.sh`
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
