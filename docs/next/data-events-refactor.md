# Data Events Refactor — Next Step

## Current State

Data events are split across two layers:
- `src/DataEvents/*.cs` — flag wrappers (`IsActive`) and some extracted logic (ActionLogging, OpponentTracking, ContactState, etc.)
- `src/Hooks/*.cs` — Harmony patches, data gathering, serialization, sending

The hooks still contain most of the logic. The event files gate whether the hook does work, but the code lives in the wrong place.

## Target

Each event file contains everything: Harmony patch registration, data gathering, serialization, and sending. The hook files are eliminated.

```
src/DataEvents/OnTurnStartEvent.cs   — Harmony prefix on AIFaction.OnTurnStart + opponent gathering + send
src/DataEvents/OnTurnEndEvent.cs     — Harmony postfix on InvokeOnTurnEnd + status gathering + transforms + send
src/DataEvents/TileScoresEvent.cs    — Harmony postfix on PostProcessTileScores + tile enumeration + send
src/DataEvents/TileModifiersEvent.cs — Harmony postfix on PostProcessTileScores + modifier application
...etc
```

## Complications

- Some patches are attribute-based (`[HarmonyPatch]`), some are manually registered in `BoamBridge.OnInitialize` via `harmony.Patch()`
- Manual patches need the event file to expose methods that BoamBridge registers
- Two events share the same Harmony target (`PostProcessTileScores`): TileScores and TileModifiers. These need separate postfix methods on the same target, or a dispatcher.
- `BoamBridge.OnInitialize` currently wires up 15+ manual patches — this registration logic needs a new home

## Files to remove after migration

- `src/Hooks/AiActionPatches.cs`
- `src/Hooks/AiObservationPatches.cs`
- `src/Hooks/PlayerActionPatches.cs`
- `src/Hooks/DiagnosticPatches.cs`

## Status

Planned. Current system works — events gate correctly, just not self-contained yet.
