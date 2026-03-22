# Tile Modifier System

Architecture for externally controlling AI unit movement and behavior through tile score injection.

## Components

### C# Side (modpack)

**`src/Engine/TileModifierStore.cs`** — Centralized `ConcurrentDictionary<string, TileModifier>` keyed by actor UUID. One combined modifier per actor.

```
TileModifier:
  AddUtility      float   — added to UtilityScore on matching tiles
  MultCombined    float   — (reserved, not yet implemented)
  MinDistance      float   — only apply to tiles at this distance or further (distance gating mode)
  MaxDistance      float   — only apply to tiles at this distance or closer, 0=no limit (distance gating mode)
  TargetX/Z       int     — target tile coordinates, -1=disabled (target mode)
  SuppressAttack  bool    — force Idle when AI picks attack/skill behavior
```

**Two modes:**
- **Target mode** (`TargetX/Z >= 0`): gradient bonus — tiles closer to target get proportionally more utility. Unit on target gets no bonus (idles).
- **Distance gating mode** (`TargetX/Z = -1`): flat bonus to all tiles in distance range.

**`src/Hooks/TileModifierPatch.cs`** — `[HarmonyPatch] Agent.PostProcessTileScores` postfix. Reads from TileModifierStore, applies utility bonus to tiles. Runs during criterion evaluation phase.

**`src/Hooks/BehaviorOverridePatch.cs`** — `[HarmonyPatch] Agent.Execute` prefix. Runs after PickBehavior chose a behavior, before execution:
- Forces Idle when unit is on its target tile (distance < 0.5)
- Forces Idle when SuppressAttack is true and chosen behavior is not Move/Idle

**`src/Engine/CommandServer.cs`** — Routes for engine to set modifiers:
- `POST /tile-modifier` — JSON body with actor, add_utility, target_x/z, etc.
- `POST /tile-modifier/clear` — clears all modifiers

### F# Side (engine)

Not yet wired up. The engine has `HttpClient` and `CommandUrl` in `RouteContext` to POST to the bridge's command server. Planned: nodes that compute modifiers and send them to the bridge.

## Test: BOAM Letter Shapes

Hardcoded test in `BoamBridge.SetupTestTileModifiers()` — assigns each of 27 AI actors a target tile forming letters B, O, A, M sequentially across the grid.

- R1-15: **B** (left)
- R16-25: **O** (center-left)
- R26-35: **A** (center-right)
- R36-45: **M** (right)
- R46+: modifiers cleared

Test shape layout: `docs/next/tile-modifier-test-shape.txt`

Settings: AddUtility=20000, SuppressAttack=true.

## Results

- Units successfully navigate toward target tiles using the gradient
- Units idle when they reach their target
- Attack suppression works (some flakiness — Execute called multiple times per turn)
- Shape transitions between letters work across round boundaries

## Known Issues

- Attack suppression: `Agent.Execute` prefix can miss some calls when the agent re-evaluates mid-turn
- The modifier is applied in `PostProcessTileScores` which runs per evaluation cycle — agents can be evaluated multiple times per turn
- Engine-to-bridge communication for modifiers not yet implemented (test uses hardcoded shapes)

## Next Steps

- Wire up F# engine to send modifiers via command server
- Implement RoamingChill node: when unit has no detected opponents, send a distance-based modifier to encourage movement
- Remove hardcoded test shapes


