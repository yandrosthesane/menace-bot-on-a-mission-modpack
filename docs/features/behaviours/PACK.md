---
order: 3
---

# Pack

## Objective

Pull units toward allies to form natural groups. When an ally is fighting, the pack converges on the threat. Prevent aimless clumping when not in combat.

```
Actor turn ends
    │
    ├─ Build ally list (same faction only, exclude self)
    │   each ally carries: position, hasActed, engaged (inRange && inContact)
    │
    ├─ Compute pack density at current position (baseline)
    │
    ├─ For each tile in actor's existing tile map:
    │   │
    │   ├─ Compute pack density at this tile
    │   │   density = sum of weighted ally influences within radius
    │   │
    │   ├─ improvement = max(0, tileDensity - currentDensity)
    │   │   (directional: only tiles that improve cohesion score positive)
    │   │
    │   └─ tile score += improvement
    │
    ▼
Tile scores added on top of roaming + reposition

Ally influence per tile:
    ┌─────────────────────────────────────────┐
    │  influence = (1 - distance/radius)      │
    │           * weight                      │
    │                                         │
    │  weight = anchoredWeight  (if acted)    │
    │         | unactedWeight   (if not)      │
    │         + contactBonus    (if engaged)  │
    └─────────────────────────────────────────┘

Crowd curve:
    ┌─────────────────────────────────────────┐
    │  score = attraction * min(density, peak)│
    │        - crowdPenalty * excess           │
    │                                         │
    │  excess = max(0, density - peak)        │
    │  (suppressed when any nearby ally is    │
    │   engaged — crowding near threats is ok)│
    └─────────────────────────────────────────┘
```

## Init boost

At battle start, `pack-init` runs with `initMultiplier` applied to density improvements. This forms packs aggressively before the first turn, then normal scoring takes over.

## Formulas

Per-ally influence at a tile:

```
dist = euclidean distance from ally to tile
influence = max(0, 1 - dist / radius) * weight
```

Weight per ally:

```
weight = (acted ? anchoredWeight : unactedWeight) + (engaged ? contactBonus : 0)
```

Density at a tile:

```
density = sum of influence across all allies within radius
```

Crowd curve:

```
score = attraction * min(density, peak) - penalty
penalty = crowdPenalty * max(0, density - peak)   // 0 if any contributing ally is engaged
```

Attraction scales with game scores:

```
attraction = max(config.attraction, gameMaxScore * config.fraction)
```

## Parameters

| Parameter | Default | Effect |
|-----------|---------|--------|
| `radius` | 20 | Influence range per ally (tiles) |
| `peak` | 4.0 | Ideal density — above this, crowd penalty applies |
| `attraction` | 560 | Floor utility per density unit |
| `fraction` | 1.2 | Scales attraction against game's max Combined score |
| `crowdPenalty` | 120 | Penalty per density above peak (suppressed near engaged allies) |
| `anchoredWeight` | 1.0 | Weight for allies that already acted this round |
| `unactedWeight` | 0.3 | Weight for allies that haven't acted yet |
| `contactBonus` | 1.5 | Extra weight for engaged allies |
| `initMultiplier` | 3.0 | Boost for round 1 pack formation |

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
