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
    │   └─ tile safety score += improvement
    │
    ▼
Tile scores added on top of roaming + reposition
```

## Ally influence

```
dist = euclidean distance from ally to tile
influence = max(0, 1 - dist / radius) * weight

weight = (acted ? anchoredWeight : unactedWeight) + (engaged ? contactBonus : 0)
```

## Crowd curve

```
score = baseSafety * min(density, peak) - penalty
penalty = crowdPenalty * max(0, density - peak)   // 0 if any contributing ally is engaged
```

Safety scales with game scores:

```
baseSafety = max(config.baseSafety, gameMaxScore * config.safetyFraction)
```

## Init boost

At battle start, `pack-init` runs with `initMultiplier` applied to density improvements. This forms packs before the first turn, then normal scoring takes over.

## Parameters

| Parameter | Effect |
|-----------|--------|
| `radius` | Influence range per ally (tiles) |
| `peak` | Ideal density — above this, crowd penalty applies |
| `baseSafety` | Floor safety per density unit |
| `safetyFraction` | Scales safety against game's max Combined score |
| `crowdPenalty` | Penalty per density above peak (suppressed near engaged allies) |
| `anchoredWeight` | Weight for allies that already acted this round |
| `unactedWeight` | Weight for allies that haven't acted yet |
| `contactBonus` | Extra weight for engaged allies |
| `initMultiplier` | Boost for round 1 pack formation |

## Presets

```json5
"pack": {
  "default": {
    "radius": 20, "peak": 4.0, "baseSafety": 280, "safetyFraction": 0.6,
    "crowdPenalty": 60, "anchoredWeight": 1.0, "unactedWeight": 0.3,
    "contactBonus": 1.5, "initMultiplier": 1.5
  }
}
```

## Examples

Spawn - Round 1

![pack_spawn.png](/docs/images/pack_spawn.png)

Round 2

![pack_round_2.png](/docs/images/pack_round_2.png)

Round 3

![pack_round_3.png](/docs/images/pack_round_3.png)
