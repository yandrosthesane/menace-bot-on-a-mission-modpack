# C# Bridge Plugin

The bridge plugin runs inside the game via MelonLoader. It observes AI decisions, player actions, and map state, then forwards everything to the tactical engine over HTTP. It also hosts the in-game minimap overlay.

## How It Works

The plugin loads automatically when the game starts. No configuration is needed -- it hooks into the game's AI evaluation loop and player input, captures the tactical map at mission prep, and sends all data to the tactical engine on port 7660.

The bridge also runs a command server (port 7661) that accepts replay actions from the engine.

## Port Settings

See [Configuration](README_CONFIG.md) for port settings (`port`, `bridge_port`, `command_port` in `config.json5`).

## Minimap

The bridge hosts an in-game minimap overlay. See [Tactical Minimap](README_MINIMAP.md) for controls and display presets.

## Technical Details

See [docs/README_BRIDGE_PLUGIN.md](docs/README_BRIDGE_PLUGIN.md) for Harmony hooks, data flow diagrams, and file descriptions.
