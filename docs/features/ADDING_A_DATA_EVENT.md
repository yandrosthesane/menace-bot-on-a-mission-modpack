---
order: 8
---

# Adding a data event

## 1. Create the event file

Create `src/DataEvents/YourEvent.cs`:

```csharp
namespace BOAM.DataEvents;

static class YourEvent
{
    internal static bool IsActive => Boundary.DataEvents.Your;
}
```

If the event has logic beyond a flag check, add methods:

```csharp
static class YourEvent
{
    internal static bool IsActive => Boundary.DataEvents.Your;

    internal static void Process(/* params from the hook */)
    {
        if (!IsActive) return;
        // gather data, serialize, send
    }
}
```

## 2. Add the flag

In `src/Boundary/DataEvents.cs`, add the field and the switch case:

```csharp
internal static bool Your;
```

In `Init()`, add to the reset block:

```csharp
Your = false;
```

And the switch case:

```csharp
case "your-event": Your = true; break;
```

## 3. Add to the project

In `modpack.json`, add to `sources`:

```json
"src/DataEvents/YourEvent.cs"
```

## 4. Add to the config

In `configs/behaviour.json5`, add to the `dataEvents` or `inactiveDataEvents` array:

```json5
"dataEvents": [
    ...
    "your-event"
]
```

## 5. If the event needs a Harmony patch

**Attribute-based** — works for public/protected methods:

```csharp
[HarmonyPatch(typeof(Il2CppMenace.Tactical.SomeClass), "SomeMethod")]
static class YourPatch
{
    static void Postfix(SomeClass __instance)
    {
        if (!DataEvents.YourEvent.IsActive) return;
        // ...
    }
}
```

**Manual registration** — needed when the method can't be found by attribute (private, overloaded, Il2Cpp mangled). Add in `BoamBridge.OnInitialize`:

```csharp
var method = typeof(Il2CppMenace.Tactical.SomeClass).GetMethod("SomeMethod", flags);
if (method != null)
    harmony.Patch(method, postfix: new HarmonyMethod(typeof(YourEvent), nameof(YourEvent.OnSomeMethod)));
```

If attribute lookup fails silently, use the name-search fallback pattern (see `LaunchMission` registration for an example).

## 6. If the event sends data to the F# engine

Add a handler in `boam_tactical_engine/Boundary/HookHandlers.fs`:

```fsharp
let private handleYourEvent (ctx: RouteContext) (root: JsonElement) =
    // parse payload, write to state store
    Results.Ok({| hook = "your-event"; status = "ok" |}) :> IResult
```

Register it in `registerHooks()`:

```fsharp
hookDispatch.["your-event"] <- handleYourEvent
```

If the event writes new data, add a state key in `Nodes/Keys.fs`:

```fsharp
let yourData : StateKey<YourType> = perSession "your-data"
```

## 7. Wire the gate

In the hook that produces this data, check the flag before doing work:

```csharp
if (!DataEvents.YourEvent.IsActive) return;
```

Or call the event's method if the logic was moved into the event file.

## 8. Update the engine banner

In `boam_tactical_engine/Program.fs`, add the event name to `allDataEvents`:

```fsharp
let allDataEvents = [
    ...
    "your-event"
]
```

## 9. Bump config version

In `configs/behaviour.json5`, increment `configVersion`. This forces user configs to fall back to the mod default so the new event is picked up.

## Active vs inactive

Put the event in `dataEvents` if it should be on by default (core functionality, minimap, behaviours). Put it in `inactiveDataEvents` if it's optional or WIP (logging, heatmaps, experimental features). The user moves entries between the two lists to enable/disable.

## Existing events

| Event | Flag | Purpose |
|-------|------|---------|
| `on-turn-start` | OnTurnStart | Faction state to engine |
| `on-turn-end` | OnTurnEnd | Actor status to engine |
| `movement-finished` | MovementFinished | Position update to engine |
| `actor-changed` | ActorChanged | Active actor notification |
| `scene-change` | SceneChange | Scene transition |
| `battle-start` | BattleStart | Battle session init |
| `battle-end` | BattleEnd | Battle session cleanup |
| `tactical-ready` | TacticalReady | Dramatis personae + init |
| `preview-ready` | PreviewReady | Map capture signal |
| `contact-state` | ContactState | Per-actor detection state |
| `movement-budget` | MovementBudget | AP/skill/movement cost |
| `objective-detection` | ObjectiveDetection | Mission objective targets |
| `tile-modifiers` | TileModifiers | Tile score modifier pipeline |
| `opponent-tracking` | OpponentTracking | Faction opponent list |
| `tile-scores` | TileScores | Tile evaluation data to engine |
| `decision-capture` | DecisionCapture | AI decisions to engine |
| `minimap-units` | MinimapUnits | Unit positions for minimap |
| `action-logging` | ActionLogging | Player and AI action events |
| `combat-logging` | CombatLogging | Damage / element hit events |
