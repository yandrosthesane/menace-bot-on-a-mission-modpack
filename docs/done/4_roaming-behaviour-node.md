# Roaming Behaviour Node — DONE

Completed 2026-03-22.

## What Was Built

### RoamingBehaviour Node (`Nodes/RoamingBehaviour.fs`)
- Registered on `OnTurnEnd.Prefix`
- Reads: `ai-actors`, `turn-end-actor`, `tile-modifiers`
- Writes: `tile-modifiers`
- On first run: initializes modifiers for all AI actors (50% SHORT maxDist=3, 50% FULL maxDist=0 for testing)
- On each turn-end: logs full actor status, preserves existing modifiers (accumulate, not overwrite)

### Turn-End Actor Status
Full actor data sent from C# to engine on every `InvokeOnTurnEnd`:

```json
{
  "round": 3,
  "faction": 7,
  "actor": "wildlife.alien_dragonfly.1",
  "tile": {"x": 28, "z": 40},
  "status": {
    "ap": 60, "apStart": 100,
    "hp": 60, "hpMax": 60,
    "armor": 60, "armorMax": 60,
    "vision": 8, "concealment": 0,
    "morale": 80, "moraleMax": 80,
    "suppression": 0,
    "isStunned": false, "isDying": false, "hasActed": false
  },
  "skills": [
    {"name": "active.fire_alien_needle_spitter_dragonfly", "apCost": 40, "minRange": 1, "maxRange": 7, "idealRange": 3}
  ]
}
```

### ActorStatus Type (Engine Side)
Added to `Domain/GameTypes.fs`:
- `SkillInfo`: Name, ApCost, MinRange, MaxRange, IdealRange
- `ActorStatus`: Actor, Faction, Position, Ap, ApStart, Hp, HpMax, Armor, ArmorMax, Vision, Concealment, Morale, MoraleMax, Suppression, IsStunned, IsDying, HasActed, Skills

### State Key
- `turn-end-actor` (PerFaction): last actor status received, written by route handler, read by roaming node

### TileModifier Type Extended
Added `MinDistance` and `MaxDistance` fields to engine-side `TileModifier` type. Serialized to bridge JSON.

### Consolidated Turn-End Hook
- `AiActionPatches.OnTurnEnd` now handles all factions (was AI-only)
- Removed `Patch_Diagnostics.OnTurnEnd` (was duplicate)
- Sends full actor status in payload
- Sets `TileModifierStore.SetPending()` before async POST

### Route Handler Passthrough
- `/hook/on-turn-end` parses actor status from payload
- Writes `ActorStatus` to store via `turnEndActor` key
- Runs walker, then flushes modifiers to bridge

## Test Results

### Movement
- +100 utility makes units move (beats Idle(1))
- Units move 1-3 tiles per turn regardless of SHORT/FULL setting
- Flat utility doesn't create a gradient — AI picks nearest tile above threshold

### Attack
- AI correctly attacks when in range (InflictDamage scores 2500-4000, far above +100 utility)
- Confirmed: AI can move → re-evaluate → attack within one turn
- Roaming modifier does NOT override attack decisions

### Actor Data
- All wildlife units: 100 or 120 AP start, cheapest attack 40 AP
- Dragonflies: vision 8, concealment 0, one ranged attack (range 1-7)
- Spiderlings: vision 8, concealment 2, one melee attack (range 1-1)
- Stingers: vision 8, concealment 2
- Warriors: vision 8, concealment 1, one melee attack (range 1-1)

## Known Issues / Future Work

### Distance Scaling
Current: flat utility bonus to all tiles at distance >= MinDistance. Need: utility that scales with distance to encourage further movement. Should be done in the roaming node, not the C# patch.

### Per-Unit Movement Cost
Different units have different movement costs per tile (terrain, unit type). The engine needs this data from the bridge to compute proper AP-aware distances. Currently we only have `DistanceToCurrentTile` (Chebyshev distance, not AP-based).

### AP-Aware Roaming
Goal: move as far as possible while keeping enough AP for one attack. Requires knowing:
- Actor's current AP (have it)
- Cheapest attack AP cost (have it)
- Movement AP cost per tile (don't have it — varies by terrain and unit)

### Modifier Accumulation
Current: roaming node sets modifiers for all actors on init, then preserves. Future: multiple nodes should be able to contribute to the same actor's modifier (merge/accumulate).
