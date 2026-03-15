# Icon Asset Pipeline (`boam_asset_pipeline/`)

Generates properly sized icon assets from game badge art for use in heatmap unit overlays and the tactical minimap.

## Overview

Game assets are **not shipped** with BOAM. You need to extract or locate the source PNGs yourself. The badges and faction icons used in the default config can be found in the game's extracted data at:

```
<ExtractedDataPath>/Assets/Resources/ui/sprites/badges/
<ExtractedDataPath>/Assets/Resources/ui/sprites/factions/
```

Copy the PNGs you want into `UserData/BOAM/`, point `icon-config.json` sources at it, and run the generator.

## File Locations & Load Order

BOAM uses a two-tier config system. User configs in `UserData/BOAM/` take precedence over mod defaults in `Mods/BOAM/configs/`. This lets you version your personal settings separately from the mod.

### Load order (checked first → fallback)

| File | User (persistent) | Mod default (reset on deploy) |
|------|-------------------|-------------------------------|
| All configs | `UserData/BOAM/configs/` | `Mods/BOAM/configs/` |
| Source art (badges, factions) | `UserData/BOAM/badges/`, `UserData/BOAM/factions/` | — |
| Generated icons | `Mods/BOAM/icons/factions/`, `Mods/BOAM/icons/templates/` | — |

### Config versions

Each versioned config has a `configVersion` field. When the mod default version is higher than the user config version, the mod default is used and a warning is logged. Update your user config to match the new structure when this happens.

| Config file | Current version | Description |
|-------------|:-:|---|
| `tactical_map.json5` | 1 | Minimap keybindings, visual defaults, layout |
| `tactical_map_presets.json5` | 1 | Display presets, map styles, entity styles, anchors |
| `config.json` | 1 | Tactical engine ports, rendering, heatmap settings |
| `icon-config.json` | 1 | Icon generation sources and mappings |

All configs live under `configs/` in both locations:
- **User**: `UserData/BOAM/configs/` (persistent, checked first)
- **Mod default**: `Mods/BOAM/configs/` (reset on deploy, fallback)

### Why two tiers?

- `Mods/BOAM/` is **wiped on every deploy** — config files there are mod defaults, not user settings
- `UserData/BOAM/` **survives deploys** — put your customised configs here
- If no user config exists, the mod default is used (first-install experience)

### Override path

Set the `BOAM_PERSISTENT_ASSETS` environment variable to override the `UserData/BOAM/` path entirely. Useful for non-standard Steam installs or development:

```bash
export BOAM_PERSISTENT_ASSETS=/path/to/my/boam/assets
```

If unset, defaults to `<game_dir>/UserData/BOAM/` where `<game_dir>` is the Menace install directory.

## Running the Generator

Run from the BOAM mod folder. It reads `icon-config.json` and resizes every source PNG to the configured output location.

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

**Options:**

| Flag | Description |
|------|-------------|
| `--force` | Overwrite existing icons (default: skip existing files) |
| `--config <path>` | Path to config file (default: `icon-config.json` next to the executable) |
| `--help` | Show usage |

By default, existing icons are preserved — only missing icons are generated. Use `--force` after updating source art or changing icon sizes.

## When to Generate

- **First install** — run once after extracting the tactical engine archive
- **After updating source art** — run with `--force` to regenerate from new sources
- **After adding new entries** — run normally (only new icons are generated)
- **After deploy (development)** — deploy wipes the icons directory, regenerate after every deploy

## Fallback Chain

When rendering a unit on the heatmap or minimap overlay, the icon is resolved in this order:

1. **Leader** — `icons/templates/{leader_name}.png` (e.g., `rewa.png`, `exconde.png`)
2. **Template** — `icons/templates/{template_name}.png` (e.g., `alien_stinger.png`)
3. **Faction** — `icons/factions/{faction}.png` (e.g., `wildlife.png`)
4. **Colored square** — hard fallback if no icon found

When a leader or template icon is missing, the engine auto-copies the faction icon to the expected path. This seeds placeholder files with the correct filenames — replace them with proper art whenever you want.

## icon-config.json

The config file declares where source art lives and how each icon maps from source to output.

### Structure

Replace `user` with your system username in all paths below.

**Linux:**
```json
{
  "defaults": {
    "size": 64,
    "output_base": "/home/user/.steam/steam/steamapps/common/Menace/Mods/BOAM/icons"
  },
  "sources": {
    "native": "/home/user/.steam/steam/steamapps/common/Menace/UserData/BOAM",
    "custom": "/home/user/my-boam-icons"
  },
  "factions": [ ... ],
  "templates": [ ... ],
  "leaders": [ ... ]
}
```

**Windows:**
```json
{
  "defaults": {
    "size": 64,
    "output_base": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Menace\\Mods\\BOAM\\icons"
  },
  "sources": {
    "native": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Menace\\UserData\\BOAM",
    "custom": "C:\\Users\\user\\my-boam-icons"
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
| `sources` | Named source directories. Each entry in the icon lists references a source by its label. |
| `factions` | Faction-level icons — one per faction. Output goes to `factions/`. |
| `templates` | Unit template icons — one per unit archetype. Output goes to `templates/`. |
| `leaders` | Leader icons — one per named character. Output also goes to `templates/` (leaders override templates in the fallback chain). |

### Entry Format

Each entry in `factions`, `templates`, and `leaders`:

```json
{ "dir": "<source_label>", "source": "<relative_path>", "output": "<relative_output>", "size": 64 }
```

| Field | Required | Description |
|-------|----------|-------------|
| `dir` | yes | Label from `sources` — resolves to the base directory |
| `source` | yes | Path relative to the source directory |
| `output` | yes | Path relative to `output_base` |
| `size` | no | Override `defaults.size` for this entry |

### Examples

**Faction icon:**
```json
{ "dir": "native", "source": "factions/enemy_faction_01.png", "output": "factions/wildlife.png" }
```

**Template icon:**
```json
{ "dir": "native", "source": "badges/squad_badge_bugs_stinger_234x234.png", "output": "templates/alien_stinger.png" }
```

**Custom art:**
```json
{ "dir": "custom", "source": "my_alien_queen.png", "output": "templates/alien_queen.png" }
```

**Larger icon (override size):**
```json
{ "dir": "native", "source": "badges/squad_badge_bugs_queen_234x234.png", "output": "templates/alien_queen.png", "size": 96 }
```

## Adding an Icon for a New Unit

1. **Identify the template name** from tactical engine logs or heatmap filenames. The engine strips the prefix before the last dot — `enemy.alien_bombardier` → `alien_bombardier`.
2. **Find or create source art** from game data or your own PNGs.
3. **Add entry to `icon-config.json`** — output filename must match the template identifier.
4. **Regenerate:** `./boam-icons --force`
5. **Restart the game** (icon cache is in-memory).

## Quick Customization Without Config

Place 64x64 PNGs directly into the icons directory:

```
Mods/BOAM/icons/
├── factions/
│   ├── wildlife.png
│   └── constructs.png
└── templates/
    ├── alien_stinger.png
    ├── rewa.png
    └── darby.png
```

The engine and minimap overlay load icons by filename — drop a PNG with the right name and restart the game.
