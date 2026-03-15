# Tactical Minimap

In-game IMGUI overlay showing unit positions on the captured map background. Units are rendered with the same icon fallback chain as heatmaps (leader → template → faction).

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

Edit `tactical_map_presets.json5` to add, remove, or modify presets.

## Unit Labels

Labels show a cleaned-up version of the unit name:
- Faction prefix stripped: `wildlife.alien_stinger.1` → `alien_stinger`
- Instance number stripped: `alien_stinger.1` → `alien_stinger`
- Common prefixes stripped: `alien_`, `construct_`, `rogue_`, `allied_`, `enemy_`
- Numeric prefixes stripped: `01_big_warrior_young` → `warrior_young`
- Noise words stripped: `big`, `small`
- Title-cased: `warrior_young` → `Warrior Young`
- Leader names used as-is: `player.carda` → `Carda`

## Icon Resolution

Same chain as the heatmap renderer:

1. `icons/templates/{leader_name}.png` (e.g., `carda.png`)
2. `icons/templates/{template_name}.png` (e.g., `alien_stinger.png`)
3. `icons/factions/{faction}.png` (e.g., `wildlife.png`)
4. Colored dot fallback (faction color from entity style)

Icons are loaded from disk at initialization — no dependency on Unity textures surviving scene transitions.

## Data Source

The minimap reads from `TacticalMapState`, a shared singleton updated by game hooks:

- **Initial population** — all actors enumerated at tactical-ready via `EntitySpawner`
- **tile-scores hook** — full unit list refresh (fires during AI evaluation)
- **movement-finished hook** — single actor position update
- **actor-changed hook** — active actor position update

No independent game polling.

## Map Background

The map texture is captured at mission prep (`OnPreviewReady`), saved as `mapbg.png` in the battle session directory, and reloaded from disk when the tactical scene loads.

For maps with tile data (`mapdata.bin`), the overlay can also generate color-based or tile-based map backgrounds from display preset settings. Currently only the captured game texture is used by default presets.

## Toast Notifications

The battle session path is shown as a toast when entering tactical. Preset changes and toggle states also show brief toasts.

## Performance

- Icon styles and labels are cached per actor — resolved once, reused every frame
- Unit snapshot only rebuilds when hook data actually changes (dirty flag)
- No per-frame file I/O or string allocations in the render loop
