---
order: 4
title: Influencing AI Behaviour
---

# Influencing AI Behaviour

BOAM modifies enemy AI movement by injecting per-tile score modifiers during the game's tile evaluation. It does not replace the game's AI — it adjusts tile scores so the game's own decision-making picks different tiles.

## How it works

```
┌─────────────────── Game (C#) ───────────────────┐
│                                                  │
│  Actor turn ends                                 │
│       │                                          │
│       ▼                                          │
│  Harmony patch (OnTurnEnd)                       │
│       │                                          │
│       ├─ Gather actor status (AP, HP, position)  │
│       ├─ Enrichments (contact, movement budget)  │
│       │                                          │
│ ──────┼────────── POST /command ─────────────────│── boundary ──
│       │                                          │
└───────┼──────────────────────────────────────────┘
        ▼
┌─────────────────── Engine (F#) ──────────────────┐
│                                                  │
│  EventHandlers receives turn-end event           │
│       │                                          │
│       ├─ Store actor status + positions           │
│       │                                          │
│       ▼                                          │
│  Walker runs registered node chain               │
│       │                                          │
│       ├─ 1. Roaming      ──► Utility scores      │
│       ├─ 2. Reposition   ──► UtilityByAttacks    │
│       ├─ 3. Pack         ──► Safety scores        │
│       ├─ 4. Investigate  ──► Utility scores      │
│       │                                          │
│       ▼                                          │
│  Scores accumulated on same tile map             │
│       │                                          │
│ ──────┼───── POST /command (batch) ──────────────│── boundary ──
│       │                                          │
└───────┼──────────────────────────────────────────┘
        ▼
┌─────────────────── Game (C#) ───────────────────┐
│                                                  │
│  TileModifierStore receives per-tile modifiers   │
│       │                                          │
│       ▼                                          │
│  Next AI evaluation: PostProcessTileScores       │
│       │                                          │
│       ├─ For each tile: score += modifier[(x,z)] │
│       │  (per dimension: U, S, D, UBA)           │
│       │                                          │
│       ▼                                          │
│  Game picks highest-scoring tile as usual         │
│                                                  │
└──────────────────────────────────────────────────┘
```

The node chain is configurable. Each node reads from and writes to the same tile modifier map. Scores accumulate across nodes, each targeting independent score dimensions.

- [Roaming](behaviours/ROAMING.md) — explore outward when idle, disabled near engagement
- [Reposition](behaviours/REPOSITION.md) — move toward closest enemy at ideal attack range
- [Pack](behaviours/PACK.md) — pull toward allies, converge on engaged ones
- [Investigate](behaviours/investigate-behaviour.md) — chase last known position after losing LOS

## Configuration

All parameters live in `configs/behaviour.json5`. Config is the single source of truth — there are no fallback defaults in code.

### Execution chains

Which nodes run on each game event, in what order:

```json5
"hooks": {
  "OnTacticalReady": ["roaming-init", "pack-init"],
  "OnTurnEnd": ["roaming-behaviour", "reposition-behaviour", "pack-behaviour", "investigate-behaviour"]
}
```

Remove a node from the list to disable it. Reorder to change priority.

### Active presets

Each behaviour has named presets. The `activePresets` block selects which to use:

```json5
"activePresets": {
  "roaming": "default",
  "reposition": "default",
  "pack": "default",
  "investigate": "default"
}
```

### Preset definitions

Each behaviour section contains named presets with all tuning parameters. See the individual docs for details.

### Diagnostic logging

The C# modifier patch logs before/after best tile with full score breakdown on every application:

```
[BOAM] TileModifier actor: 50/80 tiles
  before=(10,5) U=200 S=400 D=142 A=100
  after=(12,8) U=200 S=350 D=142 A=218
  mod=U+0 S+0 D+0 A+118 SHIFTED
```

`SHIFTED` indicates the modifier changed the game's tile choice.

## Limitations

- The game's own scoring (safety, distance, attack utility) still runs alongside our modifiers
- The game decides Move vs Attack vs Idle — we influence tile attractiveness, not action selection
- The game may limit how many units engage a single target simultaneously

## Adding new behaviours

See [Adding Nodes](behaviours/ADDING_A_BEHAVIOR_NODE.md).
