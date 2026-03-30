---
order: 6
---

# Icon Setup

Icon generation is built into the tactical engine. There is no separate binary.

On first startup, if no icons are found, the engine presents an interactive setup:

1. Generate from extracted game assets (if available at `UserData/ExtractedData/Assets/`)
2. Extract the built-in fallback icon pack (embedded in the engine binary)
3. Skip (text labels used instead of icons on the minimap)

Icons are stored in `UserData/BOAM/icons/` and survive mod deploys.

## Icon resolution chain

When rendering, the minimap and heatmap renderer look for icons in this order:

1. Leader icon (e.g., `templates/rewa.png`)
2. Template icon (e.g., `templates/alien_stinger.png`)
3. Faction icon (e.g., `factions/wildlife.png`)
4. Text label fallback

## Configuration

Icon generation is configured via `icon-config.json5`. The engine checks:

1. User config: `UserData/BOAM/configs/icon-config.json5`
2. Mod default: `Mods/BOAM/configs/icon-config.json5`

### Entry format

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

## Adding new units

1. Identify the template name from engine logs or heatmap filenames
2. Find the source art in `UserData/ExtractedData/Assets/`
3. Add an entry to `icon-config.json5`
4. Delete the icons directory and restart the engine to regenerate

## Quick customization without config

Place 64x64 PNGs directly into the icons directory:

```
UserData/BOAM/icons/
├── factions/
│   ├── wildlife.png
│   └── constructs.png
└── templates/
    ├── alien_stinger.png
    ├── rewa.png
    └── darby.png
```

The engine and minimap load icons by filename — drop a PNG with the right name and restart the game.
