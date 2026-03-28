# Tactical Minimap -- Technical Reference

## TacticalMapState

The minimap reads from `TacticalMapState`, a shared singleton updated by game hooks:

- **Initial population** -- all actors enumerated at tactical-ready via `EntitySpawner`
- **tile-scores hook** -- full unit list refresh (fires during AI evaluation)
- **movement-finished hook** -- single actor position update
- **actor-changed hook** -- active actor position update

No independent game polling.

## Icon Resolution Fallback Chain

When rendering a unit on the minimap overlay, the icon is resolved in this order:

1. `icons/templates/{leader_name}.png` (e.g., `carda.png`)
2. `icons/templates/{template_name}.png` (e.g., `alien_stinger.png`)
3. `icons/factions/{faction}.png` (e.g., `wildlife.png`)
4. Colored dot fallback (faction color from entity style)

Icons are loaded from disk at initialization -- no dependency on Unity textures surviving scene transitions.

## Label Cleaning Algorithm

Labels show a cleaned-up version of the unit name:

1. Faction prefix stripped: `wildlife.alien_stinger.1` -- `alien_stinger`
2. Instance number stripped: `alien_stinger.1` -- `alien_stinger`
3. Common prefixes stripped: `alien_`, `construct_`, `rogue_`, `allied_`, `enemy_`
4. Numeric prefixes stripped: `01_big_warrior_young` -- `warrior_young`
5. Noise words stripped: `big`, `small`
6. Title-cased: `warrior_young` -- `Warrior Young`
7. Leader names used as-is: `player.carda` -- `Carda`

## Map Background

The map texture is captured at mission prep (`OnPreviewReady`), saved as `mapbg.png` in the battle session directory, and reloaded from disk when the tactical scene loads.

For maps with tile data (`mapdata.bin`), the overlay can also generate color-based or tile-based map backgrounds from display preset settings. Currently only the captured game texture is used by default presets.

## Performance

- Icon styles and labels are cached per actor -- resolved once, reused every frame
- Unit snapshot only rebuilds when hook data actually changes (dirty flag)
- No per-frame file I/O or string allocations in the render loop

## Toast Notifications

The battle session path is shown as a toast when entering tactical. Preset changes and toggle states also show brief toasts.
