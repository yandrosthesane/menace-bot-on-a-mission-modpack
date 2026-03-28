---
order: 3
---

# Pack Behaviour

Pulls units toward allies, forming natural groups. Units near combat get extra attraction. Groups converge on threats through ally-following rather than direct enemy targeting.

**Nodes**: `pack-init` (OnTacticalReady), `pack-behaviour` (OnTurnEnd)

## How It Works

Pack uses **directional density scoring**: for each reachable tile, it computes a "pack density" (weighted sum of nearby ally influences) and compares it to the density at the actor's current position. Only improvements score positive.

```
tileDensity = sum of influence(ally) for all allies within radius
improvement = max(0, tileDensity - currentDensity)
tileModifier += improvement
```

This means tiles toward denser ally clusters score higher, while tiles away from allies score 0. The actor is pulled toward its nearest allies.

### Ally Influence

Each ally within `radius` tiles contributes an influence based on distance and status:

```
influence = (1 - distance / radius) * weight
weight = anchoredWeight (if acted) or unactedWeight + contactBonus (if engaged)
```

- **Anchored** allies (already acted this round) have stable positions — stronger pull
- **Unacted** allies might still move — weaker pull
- **Engaged** allies (personally see a detected enemy) get a bonus — pack converges on the fight

### Crowd Control

When density exceeds `peak`, a crowd penalty applies — discouraging units from forming oversized blobs:

```
score = attraction * min(density, peak) - crowdPenalty * max(0, density - peak)
```

**Exception**: the crowd penalty is **suppressed** when any ally contributing to the tile's density is engaged. When converging on a threat, crowding is the point.

### Init Boost

At tactical-ready (battle start), pack-init runs with a `initMultiplier` boost to form packs aggressively in round 1. After the first turn-end, normal pack scoring takes over.

## Parameters

All configurable in `behaviour.json5` under `"pack"` presets.

### `radius` (default: 20)

Influence range per ally in tiles. Allies beyond this distance contribute 0 influence.

**Effect of increasing**: packs form across larger distances. At 30, nearly the entire map feels pack influence.

**Effect of decreasing**: tighter packs that only form between very close allies. At 10, units must be nearby to attract each other.

### `peak` (default: 4.0)

Ideal density. Below peak, each unit of density adds attraction. Above peak, crowd penalty applies.

**Effect of increasing**: larger groups tolerated before crowding kicks in. At 6.0, groups of 6-7 form naturally.

**Effect of decreasing**: smaller groups preferred. At 2.5, groups of 3+ start getting penalized (unless engaged).

### `attraction` (default: 560)

Floor utility per density unit below peak. The actual attraction scales with game scores:

```
actualAttraction = max(attraction, gameMaxScore * fraction)
```

**Effect of increasing**: stronger pull toward allies. At 800, pack cohesion dominates most game scoring.

**Effect of decreasing**: weaker attraction. Units follow game's own preferences more.

### `fraction` (default: 1.2)

Pack attraction as a fraction of the game's max Combined score for this actor.

**Effect of increasing**: pack pull strengthens for units with high game scores.

**Effect of decreasing**: pack relies more on the floor value.

### `crowdPenalty` (default: 120)

Penalty per density unit above `peak`. Only applies when no engaged ally is nearby.

**Effect of increasing**: stronger aversion to oversized groups. Units spread into multiple smaller packs.

**Effect of decreasing**: less aversion. Groups can grow larger before splitting.

**Note**: this penalty is completely suppressed when any ally contributing to the tile's density is engaged. A unit moving toward an engaged ally never sees a crowd penalty, even in a dense cluster.

### `anchoredWeight` (default: 1.0)

Influence multiplier for allies that already acted this round. Their positions are final.

### `unactedWeight` (default: 0.3)

Influence multiplier for allies that haven't acted yet. Their positions may change.

**Interaction**: early in the round, most allies are unacted (weak pull → roaming dominates). Late in the round, most are anchored (strong pull → pack tightens). This creates a natural scatter-then-regroup cycle.

### `contactBonus` (default: 1.5)

Extra influence weight for engaged allies. An engaged ally's weight becomes `anchoredWeight + contactBonus` (if acted) or `unactedWeight + contactBonus` (if not).

**Effect of increasing**: engaged allies dominate pack pull. At 3.0, one engaged ally outweighs 6 non-engaged allies.

**Effect of decreasing**: engagement has less effect on pack formation. At 0.5, engaged allies are only slightly more attractive.

### `initMultiplier` (default: 3.0)

Boost factor for pack scores at tactical-ready. Applied to density improvements on round 1 only.

**Effect of increasing**: packs form faster in round 1. At 5.0, units clump aggressively before the first turn.

**Effect of decreasing**: gentler initial pack formation. At 1.0, round 1 pack scoring is the same as subsequent rounds.

## Presets

```json5
"pack": {
  "default": {
    "radius": 20, "peak": 4.0, "attraction": 560, "fraction": 1.2,
    "crowdPenalty": 120, "anchoredWeight": 1.0, "unactedWeight": 0.3,
    "contactBonus": 1.5, "initMultiplier": 3.0
  },
  "tight": {
    "radius": 12, "peak": 3.0, "attraction": 700, "fraction": 1.5,
    "crowdPenalty": 150, "anchoredWeight": 1.0, "unactedWeight": 0.5,
    "contactBonus": 2.0, "initMultiplier": 4.0
  },
  "loose": {
    "radius": 25, "peak": 5.0, "attraction": 400, "fraction": 0.8,
    "crowdPenalty": 80, "anchoredWeight": 1.0, "unactedWeight": 0.2,
    "contactBonus": 1.0, "initMultiplier": 2.0
  }
}
```

- **default** — balanced pack cohesion with engagement convergence
- **tight** — small close packs, aggressive convergence, strong init
- **loose** — larger spread-out groups, gentler pull, weaker engagement response
