# Replay System Documentation

## Overview

The BOAM replay system records all player actions during a tactical mission as primitive UI interactions (clicks, skill activations, endturns) into a JSONL battle log. It can then replay those actions by having the C# bridge **pull** actions one at a time from the F# tactical engine, executing each only when the game is ready.

## Architecture: Pull-Based Replay

```
F# Tactical Engine (port 7660)          C# Bridge (in-game, main thread)
┌─────────────────────────┐             ┌──────────────────────────────┐
│ Replay.fs               │             │ BoamBridge.OnUpdate()        │
│  - startSession(actions) │ ◄──────── │  - POST /replay/start        │
│  - getNext(actor, round) │ ◄──────── │  - GET /replay/next          │
│  - stopSession()         │             │    (only when gates pass)    │
│                          │ ────────► │  - Execute action             │
│ Actions served one at    │  JSON      │  - Wait for gates again      │
│ a time, in order         │  response  │  - Pull next                 │
└─────────────────────────┘             └──────────────────────────────┘
```

The bridge controls the pace. It only pulls the next action when:
1. **Active actor matches** — the action's actor UUID matches the game's current active actor
2. **Not moving** — `Actor.IsMoving()` returns false
3. **No skill animation** — `Time.time >= SkillAnimationEndTime` (set by `InvokeOnAttackTileStart` duration)
4. **Player faction** — faction is 1 (Player) or 2 (PlayerAI)

## Recording: Primitive UI Interactions

Harmony patches on concrete `TacticalAction` subclasses capture every player click:

| Patch | Hook | Records |
|-------|------|---------|
| `Patch_ClickOnTile` | `NoneAction.HandleLeftClickOnTile` | `player_click` — first click (path preview) |
| `Patch_ClickOnTile` | `ComputePathAction.HandleLeftClickOnTile` | `player_click` — second click (confirm move) |
| `Patch_ClickOnTile` | `SkillAction.HandleLeftClickOnTile` | `player_click` — skill confirm click |
| `Patch_SelectSkill` | `TacticalState.TrySelectSkill` | `player_useskill` — skill activated |
| `Patch_EndTurn` | `TacticalState.EndTurn` | `player_endturn` — turn ended |
| `Patch_ActiveActorChanged` | `TacticalState.OnActiveActorChanged` | `player_select` — actor became active |

All actions use **stable UUIDs** (e.g. `player.carda`, `wildlife.alien_stinger.2`) — no entity IDs in the F# domain.

## JSONL Format

```json
{"round":1,"faction":1,"actor":"player.carda","type":"player_click","skill":"","tile":{"x":11,"z":4}}
{"round":1,"faction":1,"actor":"player.carda","type":"player_click","skill":"","tile":{"x":11,"z":4}}
{"round":1,"faction":1,"actor":"player.carda","type":"player_endturn","skill":"","tile":{"x":11,"z":4}}
{"round":1,"faction":1,"actor":"player.lim","type":"player_select","skill":"","tile":{"x":5,"z":2}}
{"round":1,"faction":1,"actor":"player.lim","type":"player_useskill","skill":"Shoot","tile":{"x":0,"z":0}}
{"round":1,"faction":1,"actor":"player.lim","type":"player_click","skill":"","tile":{"x":8,"z":3}}
```

A move = two clicks (NoneAction preview + ComputePathAction confirm). A skill = useskill + click(s). The game's own state machine handles the interpretation.

## Replay Execution

### Command execution via BOAM Command Server (port 7661)

The bridge executes actions through `BoamCommandExecutor`:

| Action | Execution |
|--------|-----------|
| `click` | Write `m_CurrentTile` on TacticalState + `HandleLeftClickOnTile` on current action |
| `useskill` | Find skill by name → `TacticalState.TrySelectSkill(skill)` |
| `endturn` | `TacticalState.EndTurn()` |
| `select` | `TacticalController.SetActiveActor()` (resolves UUID → entityId via bridge registry) |

### Gating: SkillAnimationEndTime

`Patch_Diagnostics.OnAttackTileStart` fires when any attack begins and provides the animation duration in seconds. The bridge sets `SkillAnimationEndTime = Time.time + duration + 0.5s` and holds all commands until the animation completes. This prevents `EndTurn` during a shooting animation (which silently fails).

### Orphaned endturn skipping

When the game auto-ends a turn (e.g. all AP consumed), the recorded `player_endturn` becomes orphaned — the actor is no longer active. The engine's `getNext` detects this (action actor ≠ active actor + action is endturn) and skips it automatically.

## API

### `GET /replay/battles`
List all recorded battles with round/action counts.

### `POST /replay/start`
Start a pull-based replay session. The bridge begins pulling actions.
```json
{"battle": "battle_20260314_233650", "camera": "follow"}
```
Options:
- `camera`: `"follow"` (default) — camera centers on actor when switching units. `"free"` — camera stays where the user left it.

### `POST /navigate/replay/{battleName}?camera=follow`
Auto-navigate to tactical and start replay. Camera option via query param (default `"follow"`).

### `GET /replay/next?actor=<uuid>&round=<round>`
Bridge pulls the next action. Returns:
- `{"status": "action", "action": "click", "x": 11, "z": 4, ...}` — execute this
- `{"status": "waiting", "actor": "player.lim"}` — action is for a different actor, wait
- `{"status": "done"}` — all actions consumed

### `POST /replay/stop`
Stop the session and get results.

## Stable Actor UUIDs

All actors are identified by stable UUIDs computed at tactical-ready:
- Player units with leaders: `player.carda`, `player.rewa`
- Non-player units: `faction.template_short.N` (e.g. `civilian.worker.1`, `wildlife.alien_stinger.3`)
- Occurrence index assigned by sorting same-faction+template units by initial position `(x, z)`

The C# bridge computes UUIDs in `BuildDramatisPersonae()` and stores them in `_entityToUuid` / `_uuidToEntity` dictionaries. All hook payloads use UUIDs. The F# domain never sees entity IDs.

## Files

| File | Role |
|------|------|
| `src/BoamBridge.cs` | C# bridge: Harmony patches, pull-based replay in OnUpdate, UUID registry |
| `src/BoamCommandServer.cs` | HTTP listener on port 7661, command queue, replay start/stop |
| `src/BoamCommandExecutor.cs` | Executes click/useskill/endturn/select on main thread |
| `boam_tactical_engine/Replay.fs` | Replay session state, action parsing, getNext logic |
| `boam_tactical_engine/ActionLog.fs` | JSONL writer for battle logs |
| `boam_tactical_engine/Program.fs` | HTTP endpoints: /replay/start, /replay/next, /replay/stop |

## Getting to Tactical

Fully event-driven via auto-navigate. When the Title scene loads, the engine automatically runs:
`continuesave` → wait for MissionPreparation → `planmission` → wait for PreviewReady → `startmission` → wait for TacticalReady
