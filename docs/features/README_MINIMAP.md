---
order: 3
---

# Tactical Minimap

In-game IMGUI overlay showing unit positions on the captured map background. Units are rendered with the same icon fallback chain as heatmaps (leader -- template -- faction).


![BOAM-engine startup](/docs/images/ai_heatmap_blaster_bug_29_r01.png)

## Controls

| Key | Action | Default |
|-----|--------|---------|
| `M` | Toggle minimap on/off | Active |
| `L` | Cycle display presets | Active |

Additional keys can be enabled in `tactical_map.json5`:

| Config Key | Action |
|------------|--------|
| `MapStyleKey` | Cycle map background style independently |
| `EntityStyleKey` | Cycle entity style independently |
| `AnchorKey` | Cycle screen anchor position |
| `FogOfWarKey` | Toggle fog of war (hide undetected enemies) |
| `LabelKey` | Toggle unit name labels on/off |

Set any key to a [UnityEngine.KeyCode](https://docs.unity3d.com/ScriptReference/KeyCode.html) name to enable it. Set to `"None"` to disable.

## Display Presets

Presets combine a map style (tile size), entity style (icon size, font, colors), and screen anchor into a coherent package. Cycle with the `DisplayKey` (default `L`).

Default presets:

| Name | Tile | Icon | Font | Anchor | Target |
|------|:----:|:----:|:----:|--------|--------|
| 1080 M | 18 | 18 | 12 | bottom-right | 1080p medium |
| 1080 L | 24 | 24 | 16 | center | 1080p large |
| 4K M | 36 | 36 | 22 | bottom-right | 4K medium |
| 4K L | 48 | 48 | 28 | center | 4K large |

Add or remove presets by duplicating or commenting out entries in `tactical_map_presets.json5`. The `L` key cycles through all defined presets in order.

## Configuration

- **Keybindings and visual defaults**: `tactical_map.json5`
- **Display presets** (map styles, entity styles, anchors): `tactical_map_presets.json5`

Edit `tactical_map_presets.json5` to add, remove, or modify presets. See [Configuration](README_CONFIG.md) for the full config reference.
