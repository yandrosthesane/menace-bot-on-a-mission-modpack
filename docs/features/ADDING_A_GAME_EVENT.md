---
order: 8
---

# Adding a game event

Game events are C# data gathering hooks, each independently gatable. Files live in `src/GameEvents/`.

## 1. Create the event file

Create `src/GameEvents/YourEvent.cs`:

```csharp
namespace BOAM.GameEvents;

static class YourEvent
{
    internal static bool IsActive => Boundary.GameEvents.Your;

    internal static void Process(/* params from the caller */)
    {
        if (!IsActive) return;
        // gather data, serialize, send to engine
    }
}
```

For attribute-based Harmony patches, put the patch class in the same file:

```csharp
[HarmonyPatch(typeof(Il2CppMenace.Tactical.SomeClass), "SomeMethod")]
static class Patch_SomeMethod
{
    static void Postfix(SomeClass __instance)
    {
        if (!YourEvent.IsActive) return;
        YourEvent.Process(__instance);
    }
}
```

For manual registration (private methods, overloaded, Il2Cpp mangled), add a `Register()` method and call it from `BoamBridge.OnInitialize`:

```csharp
internal static void Register(HarmonyLib.Harmony harmony)
{
    var method = typeof(Il2CppMenace.Tactical.SomeClass).GetMethod("SomeMethod", flags);
    if (method != null)
        harmony.Patch(method, postfix: new HarmonyMethod(typeof(YourEvent), nameof(OnSomeMethod)));
}
```

## 2. Add the flag

In `src/Boundary/GameEvents.cs`, add the field:

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
"src/GameEvents/YourEvent.cs"
```

## 4. Add to the config

In `configs/game_events.json5`:

- Add to `active` if it should be on by default (core, behaviour, minimap)
- Add to `inactive` if it's optional or WIP (logging, heatmaps, experimental)

```json5
"active": [
    ...
    "your-event"
]
```

If a feature group should auto-activate this event, add it to the `FeatureDeps` mapping in `GameEvents.cs`.

## 5. If the event sends data to the F# engine

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

If a behaviour node owns this event's data, put the handler in the node file instead and dispatch from HookHandlers with one line:

```fsharp
hookDispatch.["your-event"] <- fun ctx root ->
    Nodes.YourBehaviour.handleEvent ctx.Store root
    Results.Ok({| hook = "your-event"; status = "ok" |}) :> IResult
```

## 6. If the event is an enrichment

Enrichments run inside a host event, adding data before the host sends its payload. In `configs/game_events.json5`:

```json5
"hooks": {
    "on-turn-end": ["contact-state", "movement-budget", "your-event"]
}
```

The enrichment event file implements an `Enrich()` method instead of (or in addition to) `Process()`.

## 7. Update the engine banner

In `boam_tactical_engine/Program.fs`, add the event name to `allGameEvents`:

```fsharp
let allGameEvents = [
    ...
    "your-event"
]
```

## 8. Bump config version

Increment `configVersion` in `configs/game_events.json5` so user configs fall back to the mod default.

## Existing events (20)

| Event | Purpose |
|-------|---------|
| `on-turn-start` | Faction state to engine at AI turn start |
| `on-turn-end` | Actor status to engine at turn end |
| `movement-finished` | Position update to engine after movement |
| `actor-changed` | Active actor notification |
| `scene-change` | Scene transition |
| `battle-start` | Battle session init |
| `battle-end` | Battle session cleanup |
| `tactical-ready` | Dramatis personae + init |
| `preview-ready` | Map capture signal |
| `contact-state` | Per-actor detection state (enrichment on turn-end) |
| `movement-budget` | AP/skill/movement cost (enrichment on turn-end) |
| `objective-detection` | Mission objective targets |
| `tile-modifiers` | Tile score modifier pipeline |
| `opponent-tracking` | Faction opponent list at turn-start |
| `tile-scores` | Tile evaluation data to engine |
| `decision-capture` | AI behaviour decisions |
| `minimap-units` | Unit positions (enrichment on tile-scores) |
| `los-tracking` | Mid-movement LOS detection |
| `action-logging` | Player and AI action events |
| `combat-logging` | Damage / element hit events |

Feature groups (`game_events.json5`):
- `behaviour` — activates core + behaviour events
- `minimap` — activates minimap-units, actor-changed, movement-finished, preview-ready
- `heatmaps` — activates tile-scores, decision-capture
- `logging` — activates action-logging, combat-logging, decision-capture
