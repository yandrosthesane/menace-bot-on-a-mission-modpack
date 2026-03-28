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
│  Harmony hook (OnTurnEnd)                        │
│       │                                          │
│       ├─ Gather actor status (AP, HP, position)  │
│       ├─ SyncTransforms (contact, movement)      │
│       │                                          │
│ ──────┼────────── POST /command ─────────────────│── boundary ──
│       │                                          │
└───────┼──────────────────────────────────────────┘
        ▼
┌─────────────────── Engine (F#) ──────────────────┐
│                                                  │
│  HookHandlers receives turn-end event            │
│       │                                          │
│       ├─ Store actor status + positions           │
│       │                                          │
│       ▼                                          │
│  Walker runs registered node chain               │
│       │                                          │
│       ├─ 1. Roaming     ──► per-tile scores      │
│       ├─ 2. Reposition  ──► per-tile scores      │
│       ├─ 3. Pack        ──► per-tile scores      │
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
│       │                                          │
│       ▼                                          │
│  Game picks highest-scoring tile as usual         │
│                                                  │
└──────────────────────────────────────────────────┘
```

The node chain is configurable. Each node reads from and writes to the same tile modifier map. Scores accumulate.

- [Roaming](behaviours/ROAMING.md) — explore outward when idle, disabled near engagement
- [Reposition](behaviours/REPOSITION.md) — move toward closest enemy at ideal attack range
- [Pack](behaviours/PACK.md) — pull toward allies, converge on engaged ones

## Configuration

All parameters live in `configs/behaviour.json5`.

### Hook chains

Which nodes run on each game event, in what order:

```json5
"hooks": {
  "OnTacticalReady": ["roaming-init", "pack-init"],
  "OnTurnEnd": ["roaming-behaviour", "reposition-behaviour", "pack-behaviour"]
}
```

Remove a node from the list to disable it. Reorder to change priority.

### Active presets

Each behaviour has named presets. The `active` block selects which to use:

```json5
"active": {
  "roaming": "default",
  "reposition": "aggressive",
  "pack": "tight"
}
```

### Preset definitions

Each behaviour section contains named presets with all tuning parameters. See the individual docs for details.

## Limitations

- The game's own scoring (safety, distance, attack utility) still runs alongside our modifiers
- The game decides Move vs Attack vs Idle — we influence tile attractiveness, not action selection
- The game may limit how many units engage a single target simultaneously

## Adding new behaviours

See [Adding Nodes](behaviours/ADDING_A_BEHAVIOR_NODE).
