# BOAM-modpack

The in-game mod that runs inside MelonLoader. This is what gets deployed via the Menace Modkit.

## What's in the modpack

| Feature | Description |
|---------|-------------|
| **Minimap overlay** | In-game IMGUI minimap with unit positions, icons, display presets. See [Tactical Minimap](README_MINIMAP.md). |
| **AI observation hooks** | Harmony patches that intercept AI tile scoring, behavior decisions, and movement. |
| **Player action hooks** | Captures clicks, skill use, endturn, actor selection. |
| **Map capture** | Saves the tactical map background + tile data at mission prep for the minimap and heatmaps. |
| **Combat logging hooks** | Captures per-element hit outcomes (damage, HP, suppression, morale, armor). |
| **Actor registry** | Assigns stable UUIDs to all actors (`player.carda`, `wildlife.alien_stinger.1`). |
| **Command server** | HTTP listener (port 7661) that accepts action commands from the BOAM-engine. |

## Standalone vs with BOAM-engine

The modpack works on its own — the minimap is fully functional without the engine. When the BOAM-engine is running, the modpack additionally forwards all hook data to it over HTTP for heatmap rendering and action logging.

| Mode | What works |
|------|------------|
| **BOAM-modpack only** | Minimap overlay with live unit positions, map capture, actor tracking |
| **BOAM-modpack + BOAM-engine** | Everything above + heatmap rendering, action logging, AI decision logging, auto-navigation |

## Port Settings

See [Configuration](README_CONFIG.md) for port settings (`port`, `bridge_port`, `command_port` in `config.json5`).
