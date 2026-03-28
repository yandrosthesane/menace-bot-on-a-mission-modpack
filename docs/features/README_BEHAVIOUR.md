# AI Behaviour System

BOAM's behaviour system modifies enemy AI movement by injecting per-tile utility scores during the game's tile evaluation phase. It does **not** replace the game's AI — it influences WHERE units move by adjusting tile attractiveness scores.

## How It Works

The game evaluates every reachable tile for each AI unit, scoring them on utility, safety, and distance. BOAM adds modifiers on top of these scores via `PostProcessTileScores`. The game then picks the highest-scoring tile as usual.

Three behaviour nodes run in sequence at each turn-end, each contributing to a per-tile utility map:

1. **[Roaming](behaviours/ROAMING.md)** — explores outward when idle (disabled near engagement)
2. **[Reposition](behaviours/REPOSITION.md)** — moves toward the closest known enemy at ideal attack range
3. **[Pack](behaviours/PACK.md)** — pulls units toward allies, especially those in combat

Scores from all three accumulate on the same tile map. The game sees one combined modifier per tile.

## Configuration

All behaviour parameters are in `configs/behaviour.json5`. The file has three sections:

### Hook Chains

Define which nodes run on each game event, and in what order:

```json5
"hooks": {
  "OnTacticalReady": ["roaming-init", "pack-init"],
  "OnTurnEnd": ["roaming-behaviour", "reposition-behaviour", "pack-behaviour"]
}
```

Remove a node from the list to disable it. Reorder to change execution priority.

### Active Presets

Each behaviour has named presets. The `active` block selects which preset to use:

```json5
"active": {
  "roaming": "default",
  "reposition": "aggressive",
  "pack": "tight"
}
```

### Preset Definitions

Each behaviour section contains named presets with all tuning parameters. See the individual behaviour docs for parameter details.

## Score Scaling

Modifiers scale relative to the game's own tile evaluation scores. Each behaviour has a `fraction` parameter:

```
modifier = max(default, gameMaxScore * fraction)
```

- The default value is a floor — guarantees a minimum influence level
- When the game produces higher scores (e.g. for stronger units), the modifier scales up proportionally
- This prevents hardcoded values from being too weak or too strong for different unit types

## Engagement Detection

Two signals determine if a unit is "engaged":

- **inRange** — an opponent is within this actor's personal vision radius
- **inContact** — the game's detection system has detected an opponent (accounts for line-of-sight, concealment)
- **engaged** = `inRange AND inContact` — this specific unit personally sees a detected enemy

Engagement status is computed by the C# bridge from live game objects and sent to the engine with each turn-end event.

## What We Don't Control

- The game's own tile evaluation (Safety, Distance, UtilityByAttacks) still runs
- The game picks the behaviour type (Move, Attack, Idle) — we only influence tile attractiveness
- The game may have built-in attack spreading that limits how many units engage a single target
- Our modifiers affect WHERE a unit moves, not WHETHER it attacks

## Adding New Behaviours

See [Adding Nodes](behaviours/ADDING_NODES.md) for a step-by-step guide.
