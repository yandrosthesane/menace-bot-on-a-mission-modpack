# Replay System Documentation

## Overview

The BOAM replay system records all player actions during a tactical mission into a JSONL battle log, then can replay those actions automatically using DevConsole commands via the game bridge.

## Components

### 1. C# Bridge Plugin (`BOAM-modpack/src/BoamBridge.cs`)

Harmony patches on `TacticalManager` capture player actions:

| Patch | Hook | Captures |
|-------|------|----------|
| `Patch_SkillUse` | `InvokeOnSkillUse` | `player_skill` — skill name + target tile |
| `Patch_MovementFinished` | `InvokeOnMovementFinished` | `player_move` — destination tile |
| `Patch_Movement` | `InvokeOnMovement` | `player_embark` (MovementAction.Enter) / `player_disembark` (MovementAction.Leave) |
| `Patch_EndTurn` | (endturn detection) | `player_endturn` — actor position |

**Dedup logic:** `Patch_Movement` sets `_lastDisembarkActorId` when detecting `Leave`, so `Patch_MovementFinished` skips the duplicate `player_move` for the same actor.

### 2. F# Tactical Engine (`boam_tactical_engine/`)

**Action logging** (`ActionLog.fs`): Writes JSONL entries with `type`, `template`, `tile`, `skillName`, `vehicleId`.

**Replay engine** (`Replay.fs`): Reads JSONL, maps actions to DevConsole commands:

| JSONL Action | Console Command |
|-------------|----------------|
| `player_move` | `move <x> <z>` |
| `player_skill` | `useskill "<name>" <x> <z>` |
| `player_embark` | `embark <vehicleId>` |
| `player_disembark` | `disembark <x> <z>` |
| `player_endturn` | `endturn` |

Skill names with spaces are always quoted in the command for safety.

**Endpoints:**
- `GET /replay/battles` — list recorded battles
- `POST /replay/run` — replay player actions from a battle log

### 3. DevConsole Commands (`src/Menace.ModpackLoader/SDK/DevConsole.cs`)

All commands run on the main thread via `EnqueueMainThread()`.

| Command | Description |
|---------|-------------|
| `who` | Show active actor ID, template, position, contained status |
| `move <x> <z>` | Move actor one tile (pathfinding). Returns false if blocked or no AP. |
| `useskill <name> <x> <z>` | Execute skill via `TacticalState.TrySelectSkill()`. Quote names with spaces. |
| `embark <vehicleId>` | Embark active actor into vehicle via `Entity.ContainEntity()` |
| `disembark <x> <z>` | Disembark active actor to tile via `Entity.EjectEntity()` |
| `endturn` | End active actor's turn |
| `select <id>` | Select actor by entity ID (**unreliable** — doesn't always change game UI) |

## Skill Execution Flow

The `useskill` command uses the game's native execution path:

```
1. Get active actor → get SkillContainer → iterate GetAllSkills()
2. Find skill by ID or Title (case-insensitive match)
3. Cast BaseSkill pointer to Skill type (Il2Cpp pointer constructor)
4. Get TacticalState singleton via GameType.Find("Menace.States.TacticalState")
5. Call TacticalState.TrySelectSkill(skill)
   → Self-targeted skills (Deploy, Get Up) execute immediately
   → Aimed skills create a SkillAction
6. If SkillAction exists, call HandleLeftClickOnTile(tile)
```

**Critical:** `TacticalState` is in `Menace.States`, NOT `Menace.Tactical`.

## Tested Action Types

All tested on 2026-03-14:

| Action | Console Command | Works | BOAM Logged |
|--------|----------------|-------|-------------|
| Move | `move 4 2` | Yes | Yes (`player_move`) |
| Deploy | `useskill "Deploy" 8 1` | Yes | Yes (`player_skill`) |
| Get Up | `useskill "Get Up" 8 1` | Yes | Yes (`player_skill`) |
| Vehicle Rotation | `useskill "Vehicle Rotation" 36 5` | Yes | Yes (`player_skill`) |
| Embark | `embark 1` | Yes | No (bypasses InvokeOnMovement) |
| Disembark | `disembark 4 3` | Yes | No (bypasses InvokeOnMovement) |
| End Turn | `endturn` | Yes | Yes (`player_endturn`) |

**Note on embark/disembark logging:** Console commands use `ContainEntity`/`EjectEntity` directly, bypassing the game's movement system. Manual (game UI) embark/disembark IS captured by Harmony patches. For replay, the console commands reproduce the same effect.

## Known Issues

1. **Vehicle Rotation logs twice** — duplicate `player_skill` entries in BOAM log
2. **`select` unreliable** — `SetActiveActor()` doesn't always change the game's active actor. Use `endturn` to cycle through units.
3. **`EntityCombat.UseAbility` broken** — still uses `skill.Use()` which doesn't exist on Il2Cpp `BaseSkill`. Only affects the Lua API, not DevConsole.

## Getting to Tactical for Testing

```
continuesave → sleep 7s
planmission   → sleep 14s
startmission  → sleep 14s
→ In Tactical
```

Timings from `.claude/skills/timing.json` under `tactical-chain`.

## Mission Map Reference (Current Save)

- **ID 1** = Rewa (vehicle at 4,4)
- **ID 2** = Exconde (vehicle at 36,4)
- **ID 3** = Lim (infantry at 5,2)
- **ID 4** = Carda (infantry at 8,1) — usually first active actor
