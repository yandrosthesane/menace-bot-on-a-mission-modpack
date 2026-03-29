# Game Events Completion

## Done (this session)

1. PopulateInitialUnits → MinimapUnitsEvent.PopulateInitial()
2. ReloadMapFromDisk → PreviewReadyEvent.ReloadFromDisk()
3. Lifecycle logic → event Process() methods (SceneChange, BattleEnd, BattleStart, TacticalReady)
4. SyncTransforms → ContactStateEvent + MovementBudgetEvent, SyncTransforms.cs deleted

## Remaining

### 5. Config-driven enrichment hooks

OnTurnEndEvent hardcodes calls to ContactStateEvent.Enrich() and MovementBudgetEvent.Enrich(). TileScoresEvent hardcodes a call to MinimapUnitsEvent.PopulateOverlay().

**Solution:** `game_events.json5` gains a `hooks` section:

```json5
"hooks": {
  "on-turn-end": ["contact-state", "movement-budget"],
  "tile-scores": ["minimap-units"]
}
```

At init, the system reads hooks, resolves event names to Enrich callbacks, registers them on host events. Adding a new enrichment = create file, add to config.

### 6. Config unification

**Delete:**
- `modpack.json5` — empty

**Rename:**
- `engine.json5` rendering section → `heatmaps.json5`
- `engine.json5` reduced to network ports only

**Unify feature flags:**

`game_events.json5` becomes the single source of truth for what's active. F# engine feature flags (`heatmaps`, `action_logging`, `ai_logging`, `criterion_logging`) are removed. The F# engine derives what to do from the active events list.

**Features as convenience layer:**

`game_events.json5` gains a `features` array that expands to a set of events when the config is first seeded. The `active` list is the final authority — users edit it directly.

```json5
{
  "configVersion": 2,

  // Features expand to a set of events when config is first created.
  // After that, "active" is the source of truth — edit it directly.
  "features": ["behaviour", "minimap"],

  // Feature → event mapping:
  //   behaviour → on-turn-start, on-turn-end, contact-state, movement-budget,
  //               tile-modifiers, opponent-tracking, tile-scores,
  //               battle-start, battle-end, tactical-ready, scene-change
  //   minimap   → minimap-units, actor-changed, movement-finished, preview-ready
  //   heatmaps  → tile-scores, decision-capture
  //   logging   → action-logging, combat-logging, decision-capture

  "active": [
    "on-turn-start", "on-turn-end", "contact-state", "movement-budget",
    "tile-modifiers", "opponent-tracking", "tile-scores",
    "battle-start", "battle-end", "tactical-ready", "scene-change",
    "minimap-units", "actor-changed", "movement-finished", "preview-ready"
  ],

  "inactive": [
    "objective-detection", "decision-capture", "action-logging", "combat-logging"
  ],

  "hooks": {
    "on-turn-end": ["contact-state", "movement-budget"],
    "tile-scores": ["minimap-units"]
  }
}
```

**Target config layout (7 → 5 files):**

| File | Content |
|------|---------|
| `engine.json5` | Network ports only |
| `game_events.json5` | Features, active/inactive events, enrichment hooks |
| `behaviour.json5` | Hook chains, active presets, behaviour tuning |
| `heatmaps.json5` | Rendering settings (tile pixels, gamma, fonts, borders, faction colors) |
| `tactical_map.json5` | Minimap keybindings, visual defaults |
| `tactical_map_presets.json5` | Display presets |
| `icon-config.json5` | Icon source mappings |

`modpack.json5` deleted. `engine.json5` slimmed down. Rendering extracted to `heatmaps.json5`.

## Status

Points 1-4 done. Points 5-6 planned — requires config rethink before implementation.

