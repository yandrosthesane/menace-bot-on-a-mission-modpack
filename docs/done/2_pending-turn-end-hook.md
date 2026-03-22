# Pending: Turn-End Hook for Tile Modifiers

## Goal

Send actor turn-end data from the C# modpack to the F# engine on every actor's turn end.
The engine computes tile modifiers and POSTs them to the bridge before the next turn.

## Current State

### Done (engine side — Routes.fs)
- `/hook/on-turn-end` route added — accepts `{round, faction}`, calls `sendTileModifiers`
- `sendTileModifiers` sends shape test modifiers to bridge via `POST /tile-modifier`
- Actor UUIDs registered at `tactical-ready` from dramatis personae
- Shape data (B, O, A, M) and round-based switching logic

### Not done (C# side)
- No code sends `POST /hook/on-turn-end` to the engine yet

## Open Questions

1. **Where should the turn-end hook live?**
   - New patch class in `src/Hooks/` (e.g. `TurnEndHook.cs`)?
   - Or extend an existing file?
   - `InvokeOnTurnEnd(Actor)` on `TacticalManager` fires per-actor for all factions

2. **What data to send?**
   - Minimum: round, faction, actor UUID, tile position
   - More: all unit positions? opponent state?

3. **Synchronous or async?**
   - User requirement: "engine should wait before a new turn"
   - Option A: all turn-end POSTs synchronous (blocks game thread)
   - Option B: only the last player actor's turn-end is synchronous
   - Option C: async but engine signals readiness, `OnTurnStart` prefix waits

4. **Patch registration**
   - `InvokeOnTurnEnd` is already patched twice (Patch_Diagnostics, AiActionPatches)
   - Add a third postfix? Or consolidate?

## Proposed Flow

```
Player clicks End Turn
  → InvokeOnTurnEnd fires for player actor
  → New TurnEndHook.cs POSTs to engine /hook/on-turn-end
  → Engine computes modifiers, POSTs to bridge /tile-modifier (synchronous)
  → Engine responds
  → [modifiers now in TileModifierStore]
AI faction OnTurnStart
  → AI evaluates tiles with modifiers already in place
```
