# Replay System — Known Issues & Fix Plan

Status: In progress (2026-03-14)

## Issue 0: Stable actor identifiers (foundation)

**Problem:** The game assigns dynamic entity IDs at mission spawn time. These IDs change between loads, making JSONL logs and replay commands non-portable. Every downstream fix (replay, log diffing, heatmap comparison) has to deal with ID translation.

**Fix:** Define a persistent actor UUID scheme, resolved once at battle start and used in all logs and replay commands.

**Format:**
- `player.carda` — player units use leader name (always unique)
- `player.rewa` — player vehicles use leader name too
- `wildlife.alien_stinger.1`, `wildlife.alien_stinger.2` — non-player units use `faction.template.occurrence`
- Occurrence index assigned by sorting units of the same faction+template by initial position `(x, z)` — deterministic from same seed

**Where it's used:**
- JSONL action logs (`round_log.jsonl`) — `actor` field becomes the stable UUID instead of template name
- Hook payloads (`/hook/player-action`, `/hook/tile-scores`, etc.) — carry the UUID
- Replay commands — `select` uses UUID → resolved to current entity ID at replay time
- Embark — `vehicleId` in logs becomes the vehicle's UUID, resolved at replay time

**Implementation:**
1. At `battle-start` hook (or first `on-turn-start`), the C# bridge sends a full actor roster: `{entityId, template, faction, leaderName, position}` for every actor on the map
2. The tactical engine builds a `Map<entityId, stableUuid>` and a `Map<stableUuid, entityId>` for the current session
3. All subsequent hook payloads translate `entityId → stableUuid` before logging
4. At replay start, the engine queries the bridge for the current roster, builds a new `Map<stableUuid, currentEntityId>`, and uses it for all commands
5. Matching key: same `stableUuid` string — no position or template lookup needed at replay time

**DevConsole support needed:**
- New command `roster` (or extend `actors`): returns JSON array of `{entityId, template, faction, leaderName, x, z}` for all actors
- This runs on the main thread and reads from `TacticalManager.GetAllActors()`

## Issue 1: Entity IDs change between mission loads

**Problem:** Superseded by Issue 0. Once stable UUIDs are in place, entity ID mapping is handled automatically.

**Legacy note:** The original plan was to map `(template_name, initial_position)` → current entity ID at replay time. The stable UUID approach is cleaner because it centralizes the mapping and makes logs human-readable.

## Issue 2: Embark logged before move

**Problem:** When a unit walks to a vehicle and embarks, the game logs `player_embark` (from `InvokeOnMovement.Enter`) before `player_move` (from `InvokeOnMovementFinished`). On replay, the embark fires before the unit has walked to the vehicle, causing "Actor is already moving".

**Fix (implemented):** `fixEmbarkOrder` in `Replay.fs` swaps `embark, move` pairs for the same actor to `move, embark` when loading actions. This runs at read time so both `/replay/actions` and `/replay/run` see the corrected order.

**Status:** Fixed and tested. The swap logic uses a reversed accumulator pattern — `embark :: move :: acc` produces `move, embark` after `List.rev`.

## Issue 3: No validation — replay reports success on failure

**Problem:** The replay sends commands via HTTP and considers any 200 response as "success". But commands are queued to the main thread — the bridge responds immediately with "queued", and the actual result (success or failure) appears in the game log asynchronously. Failed moves, missing actors, and broken embarks all show as "succeeded".

**Fix:** Replace the fire-and-forget model with event-driven validation.

- The C# bridge already sends every player action to the tactical engine via `/hook/player-action`
- After the replay sends a command, it waits for the corresponding `/hook/player-action` to arrive at the tactical engine
- The arriving action is compared against the expected action (same actor template, same action type, same tile)
- If it matches → action confirmed, proceed to next
- If it doesn't match or doesn't arrive within a timeout → stop replay with an error

**Implementation:**
1. Add a `ConcurrentQueue<PlayerActionPayload>` (or `TaskCompletionSource`) in the tactical engine that `/hook/player-action` pushes to when a replay is active
2. The replay loop, after sending each command, awaits the next item from this queue
3. Compare received action against expected: `(actorTemplate, actionType, tileX, tileZ)` must match
4. Timeout per action (e.g., 10 seconds) — if exceeded, the action failed silently in the game
5. This completely removes `delayMs` — the action completing IS the synchronization signal

**Benefits:**
- No more arbitrary delays — replay runs as fast as the game can process
- Real validation — replay stops at the exact point where behavior diverges
- Clear error messages — "Expected player_move to (12,2) for carda, got player_move to (11,2)" or "Timeout waiting for action"

## Issue 4: Vehicle Rotation logs twice

**Problem:** Vehicle Rotation consistently produces two `player_skill` entries in the BOAM log. This causes the replay to send the skill command twice, wasting AP.

**Root cause:** Likely the game fires `InvokeOnSkillUse` twice for rotation skills — once for the skill activation and once for the visual rotation completing. Needs investigation in `BoamBridge.cs` `Patch_SkillUse`.

**Fix options:**
- **A) Dedup in logging:** Track last skill+actor+tile in `Patch_SkillUse` and skip if identical within a short window (similar to the disembark dedup pattern)
- **B) Dedup in replay:** Skip consecutive identical `player_skill` actions for the same actor

Option B is simpler and safer — it doesn't risk losing legitimate repeated skill uses (e.g., shooting the same tile twice). The dedup should only skip if the action is IDENTICAL (same actor, same skill, same tile) AND consecutive.

## Issue 5: `select` command is unreliable

**Problem:** `TacticalController.SetActiveActor()` doesn't reliably change the game's active actor. The game's turn system controls which unit is active.

**Current impact on replay:** The replay sends `select <id>` before each action group, but the game may ignore it. Actions then execute on whatever actor the game considers active.

**Fix:** Instead of `select`, the replay should rely on the game's natural turn order:
- The game auto-selects the first undone actor at round start
- After `endturn`, the game moves to the next undone actor
- The replay should verify via `who` that the active actor matches what's expected
- If wrong actor is active, the replay should `endturn` to cycle until the right actor is selected
- If the expected actor can't be found after cycling through all actors, abort

This is more robust because it works WITH the game's turn system instead of fighting it.

## Issue 6: Actor registry should be a shared service

**Problem:** Actor identity data (template, leader name, UUID) is gathered independently in every component:
- `Patch_PostProcessTileScores` builds `unitList` from scratch each time for heatmap overlays
- The replay queries `/dramatis_personae` at replay start
- Heatmap filenames use raw entity IDs (`combined_7_alien_stinger_15.png`) — meaningless across sessions
- Per-actor JSONL filenames also use entity IDs (`actor_7_15_alien_stinger.jsonl`)

**Fix:** Build the actor registry once at battle start as a shared service in BoamBridge.

- `BoamBridge.OnSceneLoaded("Tactical")` calls `EntitySpawner.ListEntities` + `UnitActor.GetLeader()` to build the full roster with UUIDs
- Exposes `BoamBridge.Instance.GetUuid(entityId)` and `BoamBridge.Instance.ActorRegistry`
- Send the full registry in the `battle-start` hook payload so the F# engine has it immediately
- All patches, heatmap renderer, action logger, and replay use UUIDs instead of raw entity IDs

**Impact on filenames:**
- Heatmaps: `combined_7_alien_stinger_15.png` → `combined_faction7.alien_stinger.1.png`
- Actor logs: `actor_7_15_alien_stinger.jsonl` → `actor_faction7.alien_stinger.1.jsonl`
- Consistent across sessions from the same save

**Status:** Documented. `/dramatis_personae` endpoint already exists. UUID scheme implemented in `ActorRegistry.fs`. Refactor to shared service deferred until replay is validated.

## Issue 7: embark/disembark commands leave inconsistent game state

**Problem:** The `embark` and `disembark` DevConsole commands use `Entity.ContainEntity()` and `Entity.EjectEntity()` directly. These are raw container operations that bypass the game's normal `TravelAndEnterAction` flow. Result:
- Unit is inside the vehicle at data level
- UI still shows the unit as selected (camera jumps to next unit but selection stays)
- Unit doesn't get proper embark/disembark skills or state transitions
- Subsequent `endturn` hits the wrong actor because game state is inconsistent

**Root cause:** The game's native embark uses `TravelAndEnterAction` which handles: pathfinding to vehicle, enter animation, UI update, camera transition, skill/state updates, and turn advancement. `ContainEntity()` only does the data part.

**Fix:** Replace the `embark` command implementation with the game's proper embark flow. Options:
- **A) TacticalState approach:** Find if there's a skill or action that triggers embark through `TacticalState`, similar to how `useskill` uses `TrySelectSkill` + `HandleLeftClickOnTile`
- **B) TravelAndEnterAction:** Construct and execute `TravelAndEnterAction` directly — this is the class the game uses when the player clicks a vehicle
- **C) Movement-based:** Use `move <vehicle_x> <vehicle_z>` when the unit is adjacent — the game may auto-embark when moving onto a vehicle tile

**Investigation needed:**
- Check what `TravelAndEnterAction` constructor takes and how it's triggered
- Check if moving onto a vehicle tile triggers auto-embark
- Check `docs/reverse-engineering/turn-action-system.md` for `TravelAndEnterAction` details
- Test option C first (simplest) — if `move` to the vehicle's tile triggers embark, we don't need the `embark` command at all for replay

**Status:** Partially implemented. `boam_click` command added using `TacticalState.GetCurrentAction().HandleLeftClickOnTile(tile)` but `HandleLeftClickOnTile` is not found on the returned `TacticalAction` base type. The Il2Cpp interop doesn't expose virtual methods on the base class — need to either:
- Cast to the concrete type (`NoneAction`) and call on that
- Or find `NoneAction` type via `GameType.Find` — but the diagnostic patches showed `NoneAction` type was NOT found via `GameType.Find("Menace.Tactical.NoneAction")`
- Or use `Il2CppMenace.Tactical.NoneAction` directly (direct Il2Cpp type, not reflection)

**Next step:** Check if `Il2CppMenace.Tactical.NoneAction` or `Il2CppMenace.Tactical.TacticalAction` is accessible as a direct type in ModpackLoader (it has Assembly-CSharp reference). If yes, construct from pointer and call `HandleLeftClickOnTile` directly without reflection.

## Issue 11: HandleLeftClickOnTile reads tile from raycast, not parameter or getter

**Discovery:** `HandleLeftClickOnTile(Tile, Actor)` ignores the Tile parameter. The game determines the target tile via mouse cursor raycast. Patching `GetHoveredTile()` to return an override doesn't work — the game reads the field directly, not through the getter.

**Diagnostic logging confirmed:** All three getters (`GetCurrentTile`, `GetTargetTile`, `GetHoveredTile`) are called during a click, but overriding their return values doesn't change the click target.

**HandleMouseMoveOnTile(Vector3, Tile, Tile, Actor):** Takes 4 params. Passing (0,0,0) for Vector3 doesn't set the hover — needs real world coordinates.

**Approaches tried and failed:**
1. Pass tile as HandleLeftClickOnTile param → ignored
2. HandleMouseMoveOnTile with zero Vector3 → no effect
3. Prefix patch on GetHoveredTile to return override → game doesn't use getter for targeting

**SOLVED:** Write directly to `m_CurrentTile` field on TacticalState before calling `HandleLeftClickOnTile`. The game reads this field (set by mouse raycast in normal play) to determine the click target.

```csharp
// Set m_CurrentTile on TacticalState
var field = ts.GetType().GetProperty("m_CurrentTile", BindingFlags.Public | BindingFlags.Instance);
field.SetValue(ts, tileProxy);
// Then call HandleLeftClickOnTile — it reads m_CurrentTile
handleClick.Invoke(currentAction, new[] { tileProxy, actorProxy });
```

**Confirmed working (no cursor dependency):**
- Movement: double-click (m_CurrentTile write + HandleLeftClickOnTile × 2)
- Embark: double-click on vehicle tile
- Disembark: double-click on adjacent tile while inside vehicle

## Implementation Order

1. **Stable actor UUIDs** (Issue 0) — foundation done: `ActorRegistry.fs`, `/dramatis_personae` endpoint, replay ID mapping. Shared service refactor (Issue 6) deferred.
2. **Event-driven sync** (Issue 3) — DONE. EventBus, scene-change hook, preview-ready hook, tactical-ready hook. `/navigate/tactical` is fully event-driven (continuesave→MissionPreparation scene→planmission→PreviewReady→startmission→TacticalReady). Replay `executeAction` uses bus for player-action confirmation. Still needs: fix `boam_click` (HandleLeftClickOnTile not found on base type).
3. **Turn-order-based actor selection** (Issue 5) — replaces unreliable `select`
4. **Vehicle Rotation dedup** (Issue 4) — minor, dedup consecutive identical skills
5. Embark order fix (Issue 2) — already done
6. **Shared actor registry service** (Issue 6) — refactor all components to use centralized UUIDs

## Issue 8: Race condition querying dramatis_personae at tactical-ready

**Problem:** The `tactical-ready` hook fires after 60 frames of init delay, then immediately queries `/dramatis_personae` from the tactical engine (HTTP thread). But the game's entity data may still be initializing on the main thread, causing `GameObj.GetGameType` to fail with "A concurrent update was performed on this collection".

**Result:** `dramatis_personae.json` has partial data — some actors have empty template names and position (0,0).

**Fix:** The dramatis personae query accesses game data from the HTTP thread which races with the main thread. Options:
- **A)** Have the C# bridge collect the roster on the main thread during `OnUpdate` (when `_ready` becomes true) and include it in the `/hook/tactical-ready` payload — no HTTP query needed.
- **B)** Add a short delay after tactical-ready before querying.

Option A is cleaner — the data is collected on the main thread where it's safe, and sent as part of the hook payload.

## Issue 9: boam_click — tile parameter is ignored, game reads cursor position

**Discovery:** `HandleLeftClickOnTile(Tile, Actor)` does NOT use the Tile parameter for targeting. The game reads the tile from the mouse cursor position (hover state). Confirmed by test: sent click to (34,0) while mouse hovered (38,0) — unit moved to (38,0).

**The Actor parameter:** Always pass the active actor. Passing null or a different actor breaks the call silently.

**Fix:** Must call `HandleMouseMoveOnTile(tile)` BEFORE `HandleLeftClickOnTile(tile)` to set the hover state. This simulates the mouse moving to the tile, which sets up the target. Then the click confirms.

**Full sequence for any tile interaction:**
1. `HandleMouseMoveOnTile(tile, actor)` — simulate hover (sets target)
2. `HandleLeftClickOnTile(tile, actor)` — first click (shows path preview)
3. `HandleLeftClickOnTile(tile, actor)` — second click (confirms action)

Both methods take `(Tile, Actor)` where Actor = active actor.

**Confirmed working for:** Movement (double-click with mouse hovering target).

**Confirmed action chains (all require cursor hover OR HandleMouseMoveOnTile):**

| Action | Sequence |
|--------|----------|
| Movement | `hover(tile)` → `click(tile)` → `click(tile)` |
| Embark | `hover(vehicleTile)` → `click(vehicleTile)` → `click(vehicleTile)` |
| Disembark | `hover(targetTile)` → `click(targetTile)` → `click(targetTile)` |
| Deploy/Get Up | `useskill "Deploy" x z` (immediate, no click needed) |
| Vehicle Rotation | `useskill "Vehicle Rotation" x z` → `click(dirTile)` → `click(dirTile)` (skill uses tile param, not cursor) |
| Vehicle Rotation | `useskill "Vehicle Rotation" x z` → `boam_click x z` × 2 (skill targets via tile param, click confirms) |
| Shoot | `useskill "Shoot" x z` activates aim preview but `boam_click` does NOT confirm. Manual mouse click works. **NEEDS INVESTIGATION** — SkillAction for shooting may need a different confirmation method. |

**Key insights:**
- `useskill` uses the tile parameter correctly for targeting (not cursor-dependent)
- `HandleLeftClickOnTile` ignores tile param and reads cursor — MUST call `HandleMouseMoveOnTile` first
- Self-targeting skills (Deploy, Get Up) execute immediately via `useskill` — no click needed
- Vehicle Rotation: `useskill` activates + `boam_click` × 2 confirms
- Shoot: `useskill` activates aim preview but `boam_click` doesn't fire. Manual click does. Needs investigation into `SkillAction` confirm method.

## Confirmed replay command mapping

| Recorded Action | Replay Command Sequence |
|----------------|------------------------|
| `player_move` | `hover(tile)` → `click(tile)` → `click(tile)` |
| `player_embark` | `hover(vehicleTile)` → `click(vehicleTile)` → `click(vehicleTile)` |
| `player_disembark` | `hover(targetTile)` → `click(targetTile)` → `click(targetTile)` |
| `player_skill` (Deploy, Get Up) | `useskill "Name" x z` (immediate) |
| `player_skill` (Vehicle Rotation) | `useskill "Name" x z` → `click(dirTile)` → `click(dirTile)` |
| `player_skill` (Shoot, Suppressive Fire) | `useskill "Name" x z` → `boam_click targetX targetZ` × 1 (single click fires) |
| `player_endturn` | `endturn` |

Where `hover(tile)` = `HandleMouseMoveOnTile(tile, activeActor)` and `click(tile)` = `HandleLeftClickOnTile(tile, activeActor)`.

Note: `player_move` after `player_embark` should be skipped (arrival event logged by MovementFinished).

## Issue 10: ActiveActorChanged patch param name

**Problem:** Harmony parameter injection requires exact name match. The Il2Cpp method has `_activeActor` but the patch declared `_actor`.

**Fix:** Rename to `_activeActor` — already done in code, needs deploy.
