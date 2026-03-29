# Data Events Refactor — Spec

## Problem

Data events are split across two layers:
- `src/DataEvents/*.cs` — flag wrappers and some extracted logic
- `src/Hooks/*.cs` — Harmony patches, data gathering, serialization, sending
- `src/BoamBridge.cs` — 15+ inline manual patch registrations

An event's code lives in 2-3 files. Adding or modifying an event requires changes in multiple places. The event files are not self-contained.

## Target Architecture

Each event file is the single source of truth for that event:
- Owns its Harmony patch class(es)
- Owns its `Register(Harmony, Logger)` method for manual patches
- Owns its data gathering and sending logic
- Checks its own `IsActive` flag
- No external caller needs to know the internals

BoamBridge.OnInitialize becomes a list of Register calls.

## Event File Contract

Every event file follows this shape:

```csharp
namespace BOAM.DataEvents;

static class SomeEvent
{
    internal static bool IsActive => Boundary.DataEvents.SomeFlag;

    // For manual patches only:
    internal static void Register(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
    {
        // find method, harmony.Patch(), log result
    }

    // Harmony postfix/prefix targets:
    public static void OnSomething(/* Il2Cpp params */)
    {
        if (!IsActive) return;
        // gather, serialize, send
    }
}
```

Attribute-based patches (`[HarmonyPatch]`) are classes inside the event file. They get picked up by `PatchAll`.

## Issues and Solutions

### 1. Manual vs attribute patch registration

**Issue:** Some game methods can't be found by `[HarmonyPatch]` attribute (private, overloaded, Il2Cpp name mangling). These need manual `harmony.Patch()` calls which currently live in BoamBridge.

**Solution:** Each event with manual patches exposes `Register(Harmony, Logger)`. BoamBridge calls it:

```csharp
_harmony.PatchAll(typeof(BoamBridge).Assembly);
OnTurnEndEvent.Register(harmony, Logger);
ActorChangedEvent.Register(harmony, Logger);
ActionLoggingEvent.Register(harmony, Logger);
// etc.
```

### 2. Shared Harmony targets

**Issue:** `Agent.PostProcessTileScores` has two postfixes: TileModifierPatch (apply modifiers) and Patch_PostProcessTileScores (observe scores). Both are attribute-based.

**Solution:** Both move to their event files as separate `[HarmonyPatch]` classes. Harmony supports multiple patches on the same target. Execution order follows `PatchAll` registration order (alphabetical by class name within the assembly). TileModifierPatch must run before PostProcessTileScores — verify by class naming or explicit Harmony priority.

**Issue:** `TacticalManager.InvokeOnMovementFinished` has one attribute patch (MovementFinishedEvent) and one manual (ActionLoggingEvent AI move).

**Solution:** Both move to their event files. PatchAll runs first (attribute), then Register (manual). Order preserved.

### 3. Mixed-concern patches

**Issue:** `AiActionPatches.OnTurnEnd` does multiple things: sends on-turn-end hook, calls ContactState/MovementBudget enrichment, sets TileModifiers pending, and logs ai-action endturn. It serves OnTurnEndEvent, ContactStateEvent, MovementBudgetEvent, TileModifiersEvent, and ActionLoggingEvent.

**Solution:** The patch method moves to `OnTurnEndEvent.cs`. It calls the other events internally:

```csharp
ContactStateEvent.Enrich(gameObj, vision, factionId, turnEndData);
MovementBudgetEvent.Enrich(actor, entity, turnEndData);
// serialize and send on-turn-end
TileModifiersEvent.SetPending();
// ai-action endturn (conditional on ActionLoggingEvent.IsActive)
```

OnTurnEndEvent orchestrates, other events provide their piece.

**Issue:** `Patch_PostProcessTileScores` serves TileScoresEvent and MinimapUnitsEvent.

**Solution:** The patch method moves to `TileScoresEvent.cs`. It calls MinimapUnitsEvent for the overlay population.

**Issue:** `Patch_ActiveActorChanged` serves ActorChangedEvent, MinimapUnitsEvent, and ActionLoggingEvent.

**Solution:** Moves to `ActorChangedEvent.cs`. Calls MinimapUnitsEvent for position update, ActionLoggingEvent for select logging.

### 4. Lifecycle events with no Harmony patches

**Issue:** SceneChange, BattleStart, BattleEnd, TacticalReady fire from BoamBridge lifecycle methods (OnSceneLoaded, OnUpdate), not from Harmony patches.

**Solution:** Each event file exposes a `Process()` method. BoamBridge calls it:

```csharp
// in OnSceneLoaded:
DataEvents.SceneChangeEvent.Process(sceneName);
DataEvents.BattleEndEvent.Process();

// in OnUpdate (tactical-ready):
DataEvents.BattleStartEvent.Process(sessionDir);
DataEvents.TacticalReadyEvent.Process(dramatisPersonae);
```

### 5. PreviewReady + LaunchMission coordination

**Issue:** Two patches (`Patch_PreviewReady`, `Patch_LaunchMission`) coordinate via shared cached state (`CachedInstance`, `CachedResult`).

**Solution:** Both move to `PreviewReadyEvent.cs`. The cached state stays private to the event file. Register wires both patches.

### 6. BoamBridge bloat

**Issue:** BoamBridge.OnInitialize is 200+ lines of patch wiring.

**Solution:** After migration it becomes:

```csharp
_harmony.PatchAll(typeof(BoamBridge).Assembly);
OnTurnEndEvent.Register(harmony, Logger);
ActorChangedEvent.Register(harmony, Logger);
PreviewReadyEvent.Register(harmony, Logger);
ActionLoggingEvent.Register(harmony, Logger);
CombatLoggingEvent.Register(harmony, Logger);
```

## Migration Map

| Event File | Absorbs From | Patch Type | Register needed |
|-----------|-------------|------------|-----------------|
| OnTurnStartEvent | AiObservationPatches.Patch_OnTurnStart | Attribute | No |
| OnTurnEndEvent | AiActionPatches.OnTurnEnd | Manual | Yes |
| TileScoresEvent | AiObservationPatches.Patch_PostProcessTileScores | Attribute | No |
| TileModifiersEvent | TileModifierPatch | Attribute | No |
| MovementFinishedEvent | AiObservationPatches.Patch_MovementFinished | Attribute | No |
| DecisionCaptureEvent | AiObservationPatches.Patch_AgentExecute | Attribute | No |
| ActorChangedEvent | PlayerActionPatches.Patch_ActiveActorChanged | Manual | Yes |
| PreviewReadyEvent | PlayerActionPatches.Patch_PreviewReady + Patch_LaunchMission | Manual | Yes |
| ActionLoggingEvent | PlayerActionPatches.Patch_EndTurn, Patch_ClickOnTile, Patch_SelectSkill + AiActionPatches.OnMovementFinished, OnSkillUse + DiagnosticPatches (all 3) | Manual | Yes |
| CombatLoggingEvent | AiActionPatches.OnElementHit | Manual | Yes |
| SceneChangeEvent | BoamBridge.OnSceneLoaded inline | None (lifecycle) | No |
| BattleStartEvent | BoamBridge.OnUpdate inline | None (lifecycle) | No |
| BattleEndEvent | BoamBridge.OnSceneLoaded inline | None (lifecycle) | No |
| TacticalReadyEvent | BoamBridge.OnUpdate inline | None (lifecycle) | No |
| OpponentTrackingEvent | Already self-contained | N/A | N/A |
| ContactStateEvent | Already self-contained | N/A | N/A |
| MovementBudgetEvent | Already self-contained | N/A | N/A |
| ObjectiveDetectionEvent | Already self-contained | N/A | N/A |

## Files to delete

- `src/Hooks/AiObservationPatches.cs`
- `src/Hooks/AiActionPatches.cs`
- `src/Hooks/PlayerActionPatches.cs`
- `src/Hooks/DiagnosticPatches.cs`
- `src/Hooks/TileModifierPatch.cs`

## Execution order

1. Attribute-based (no BoamBridge changes): TileModifiersEvent, OnTurnStartEvent, MovementFinishedEvent, DecisionCaptureEvent, TileScoresEvent
2. Manual patches: OnTurnEndEvent, ActorChangedEvent, PreviewReadyEvent, CombatLoggingEvent, ActionLoggingEvent
3. Lifecycle: SceneChangeEvent, BattleStartEvent, BattleEndEvent, TacticalReadyEvent
4. Delete empty hook files, clean BoamBridge

Deploy and test after each step.

## Verification

After each migration: deploy, enter battle, check MelonLoader log for patch registration and event firing. Final check: all hook files deleted, BoamBridge.OnInitialize is just Register calls.

## Status

Planned. Current system works — events gate correctly, just not self-contained yet.
