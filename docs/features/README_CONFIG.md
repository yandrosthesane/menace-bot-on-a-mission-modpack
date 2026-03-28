# Configuration

BOAM uses a two-tier config system with versioned JSON5 files. All configs support `//` and `/* */` comments.

## Two-Tier System

| Tier | Location | Persistence |
|------|----------|-------------|
| **User** | `<game_dir>/UserData/BOAM/configs/` | Survives deploys |
| **Mod default** | `<game_dir>/Mods/BOAM/configs/` | Reset on every deploy |

On startup, each config is loaded by checking the user location first. If a user config exists and has a `configVersion` >= the mod default, it is used. Otherwise the mod default is used and a warning is logged.

### First Install

On first run, each component automatically copies its config from `Mods/BOAM/configs/` to `UserData/BOAM/configs/` if it doesn't exist yet:

| Config | Seeded by |
|--------|-----------|
| `engine.json5` | BOAM-engine (on engine startup) |
| `behaviour.json5` | BOAM-engine (on engine startup) |
| `tactical_map.json5` | BOAM-modpack (on game launch) |
| `tactical_map_presets.json5` | BOAM-modpack (on game launch) |

No manual copying needed — just edit the user configs to customise.

### Config Versioning

Every config has a `"configVersion": N` field. When the mod's config structure changes between releases, the version is bumped. If your user config has an older version, it's skipped with a warning:

```
[BOAM] TacticalMap — User config is outdated (v1 < v2), using mod default. Update your config at: .../UserData/BOAM/configs/tactical_map.json5
```

To fix this: update your user config to match the new structure and bump `configVersion` to match the mod default. If you don't bump the version number, your user config will continue to be ignored in favor of the mod default — even if the content is correct.

## Config Files

### `engine.json5` — BOAM-engine

Controls the BOAM-engine (ports, rendering, feature toggles).

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `configVersion` | int | 2 | Config structure version |
| `port` | int | 7660 | BOAM-engine HTTP port |
| `bridge_port` | int | 7655 | Game bridge port (C# side) |
| `command_port` | int | 7661 | Command server port |
| `heatmaps` | bool | **false** | Collect render job data for offline heatmap generation |
| `action_logging` | bool | **false** | Log player actions + AI decisions to `round_log.jsonl` |
| `ai_logging` | bool | **false** | Log AI behavior decisions (requires `action_logging`) |
| `rendering.minTilePixels` | int | 64 | Minimum pixels per tile (upscaling) |
| `rendering.gamma` | float | 0.35 | Map background gamma (< 1.0 brightens shadows) |
| `rendering.fontFamily` | string | `DejaVu Sans Mono` | Font for score text |
| `rendering.scoreFontScale` | float | 0.32 | Score text size relative to tile |
| `rendering.labelFontScale` | float | 0.33 | Label text size relative to tile |
| `rendering.borders.*` | object | — | Border styles: `margin`, `thickness`, `color [R,G,B,A]` |
| `rendering.factionColors` | object | — | Per-faction colors keyed by index, `[R,G,B,A]` arrays |

#### Feature toggles

By default only the minimap is active — it works out of the box with no config changes. Enable additional features as needed:

| Goal | Config changes |
|------|---------------|
| **Minimap only** (default) | None |
| **Action logging** | `action_logging: true` |
| **Action + AI decision logging** | `action_logging: true`, `ai_logging: true` |
| **Heatmaps** | `heatmaps: true` (add `action_logging: true` for action logs in battle reports) |
| **Everything** | All three set to `true` |

### `tactical_map.json5` — Minimap Overlay

Controls the in-game IMGUI minimap.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `configVersion` | int | 1 | Config structure version |
| `Enabled` | bool | true | Enable/disable the minimap entirely |
| `ToggleKey` | string | `M` | Show/hide minimap |
| `DisplayKey` | string | `L` | Cycle display presets |
| `MapStyleKey` | string | `None` | Cycle map style (power-user) |
| `EntityStyleKey` | string | `None` | Cycle entity style (power-user) |
| `AnchorKey` | string | `None` | Cycle screen anchor (power-user) |
| `FogOfWarKey` | string | `None` | Toggle fog of war |
| `LabelKey` | string | `None` | Toggle unit labels |
| `FogOfWarDefault` | bool | true | Start with FoW on |
| `LabelsDefault` | bool | true | Start with labels visible |
| `Opacity` | float | 0.8 | Panel opacity (0.1–1.0) |
| `MapBrightness` | float | 2.0 | Map brightness (0.1–4.0) |
| `Margin` | int | 12 | Screen edge margin (px) |
| `HeaderHeight` | int | 20 | Header bar height (px) |
| `Padding` | int | 4 | Panel inner padding (px) |
| `LabelFontSize` | int | 9 | Fallback font size if EntityStyle has none |
| `HeaderFontSize` | int | 11 | Header text size (px) |
| `ToastFontSize` | int | 14 | Toast notification size (px) |

Keybinding values are [UnityEngine.KeyCode](https://docs.unity3d.com/ScriptReference/KeyCode.html) names. Common values: `M`, `L`, `F`, `Tab`, `Space`, `BackQuote` (`` ` ``). Set to `"None"` to disable a keybinding.

### `tactical_map_presets.json5` — Display Presets

Defines the presets cycled with `DisplayKey`. Four sections:

**Anchors** — screen positions:
| Field | Type | Description |
|-------|------|-------------|
| `Key` | string | Reference name |
| `X` | float 0.0–1.0 | Horizontal position (0=left, 1=right) |
| `Y` | float 0.0–1.0 | Vertical position (0=top, 1=bottom) |

**MapStyles** — map background:
| Field | Type | Description |
|-------|------|-------------|
| `Key` | string | Reference name |
| `TileSize` | int | Pixels per tile on screen |
| `TileFolder` | string (optional) | Compose from tile PNGs |
| `MapColors` | object (optional) | Generate from flat hex colors |

**EntityStyles** — unit rendering:
| Field | Type | Description |
|-------|------|-------------|
| `Key` | string | Reference name |
| `IconSize` | int | Icon size in pixels |
| `FontSize` | int | Label font size in pixels |
| `Background` | string | Panel background color `#RRGGBBAA` |
| `HeaderText` | string | Header text color |
| `FactionColors` | object | Per-faction label colors (faction name → `#RRGGBBAA`) |

**DisplayStyles** — preset combos:
| Field | Type | Description |
|-------|------|-------------|
| `Name` | string | Shown in overlay header |
| `MapStyle` | string | Key from MapStyles |
| `EntityStyle` | string | Key from EntityStyles |
| `Anchor` | string | Key from Anchors |
| `Opacity` | float (optional) | Override global opacity (0.1–1.0) |
| `MapBrightness` | float (optional) | Override global brightness (0.1–4.0) |

### `behaviour.json5` — AI Behaviour Presets

Controls the AI behaviour system: which nodes run, in what order, and with what parameters. See [AI Behaviour System](README_BEHAVIOUR.md) for full documentation.

| Section | Description |
|---------|-------------|
| `hooks` | Node chains per hook point — controls execution order |
| `active` | Selects which named preset each behaviour uses |
| `roaming` | Named presets for roaming parameters |
| `reposition` | Named presets for reposition parameters |
| `pack` | Named presets for pack parameters |

Individual behaviour parameters are documented in [Roaming](behaviours/ROAMING.md), [Reposition](behaviours/REPOSITION.md), and [Pack](behaviours/PACK.md).

### `modpack.json5` — Modpack Config

Independent config for the C# bridge, separate from the engine.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `configVersion` | int | 1 | Config structure version |
| `opponent_filter` | bool | true | Enable opponent filtering |

### `icon-config.json5` — Icon Generator

See [Icon Generator](README_ICON_GENERATOR.md) for full details.

## Current Config Versions

| Config | Version |
|--------|:-------:|
| `engine.json5` | 2 |
| `behaviour.json5` | 1 |
| `modpack.json5` | 1 |
| `tactical_map.json5` | 1 |
| `tactical_map_presets.json5` | 1 |
| `icon-config.json5` | 1 |
