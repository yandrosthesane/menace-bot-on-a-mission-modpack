# Reposition Behaviour

Moves units toward their ideal attack range from the closest known enemy. Only active near engagement. Melee units rush in, ranged units find firing positions.

**Node**: `reposition-behaviour` (OnTurnEnd)

## How It Works

When an actor is near engagement (same check as roaming suppression), reposition finds the closest known opponent and scores tiles based on how much closer they bring the actor to its ideal attack range.

```
deviation = |distanceToTarget - idealRange|
improvement = currentDeviation - tileDeviation
utility = (maxUtility / idealRange) * (improvement / currentDeviation)
```

This is **directional** — only tiles that improve the actor's position score positive. Tiles that don't help get 0.

### Melee vs Ranged

The `idealRange` comes from the actor's skills (smallest `IdealRange` across all attacks):

- **Melee** (idealRange = 1): gets full `maxUtility`. Tiles adjacent to the target score highest. Units rush toward the enemy.
- **Ranged** (idealRange = 3): gets `maxUtility / 3`. Tiles at distance 3 from the target score highest. Units manoeuvre to firing range.
- **Bombardier** (idealRange = 5): gets `maxUtility / 5`. Lighter pull toward a more distant firing position.

The inverse scaling reflects urgency — melee units are useless unless adjacent, ranged units can contribute from further away.

### Already in Position

If the actor is already within 0.5 tiles of its ideal range, reposition produces no tiles (no modifier). The game's own AI handles attack decisions from there.

## Parameters

All configurable in `behaviour.json5` under `"reposition"` presets.

### `maxUtility` (default: 600)

The floor modifier strength for melee (idealRange = 1). Ranged units get `maxUtility / idealRange`.

**Effect of increasing**: melee units push harder toward enemies, overriding the game's safety/distance preferences. At 900, melee reposition dominates most tile scores.

**Effect of decreasing**: melee units approach more cautiously, game's own scoring has more influence.

**Example**: at `maxUtility = 600`, idealRange = 1, a tile that halves the deviation gets +300. At idealRange = 3, same improvement gives +100.

### `fraction` (default: 2.0)

Reposition influence as a fraction of the game's max Combined score, for melee. The actual range-scaled utility is:

```
actualMaxUtility = max(maxUtility, gameMaxScore * fraction)
rangeScaledUtility = actualMaxUtility / idealRange
```

**Effect of increasing**: melee units become extremely aggressive. At `fraction = 3.0`, reposition can be 3x the game's strongest tile score.

**Effect of decreasing**: reposition becomes a suggestion rather than a command.

**Note**: the fraction applies to the melee-equivalent value. A ranged unit with idealRange = 3 effectively gets `gameMaxScore * fraction / 3`.

## Presets

```json5
"reposition": {
  "default":    { "maxUtility": 600, "fraction": 2.0 },
  "aggressive": { "maxUtility": 900, "fraction": 3.0 },
  "passive":    { "maxUtility": 300, "fraction": 1.0 }
}
```

- **default** — melee rushes, ranged repositions
- **aggressive** — melee overrides all other scoring, ranged pushes hard
- **passive** — gentle repositioning, game's own preferences dominate
