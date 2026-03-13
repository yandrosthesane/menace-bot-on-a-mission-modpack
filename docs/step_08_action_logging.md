# Step 8: AI Action Decision Logging

**Date:** 2026-03-13
**Status:** WORKING

## Overview

Comprehensive action logging for every AP-costing decision made by AI actors. Each decision records the chosen behavior, all evaluated alternatives with scores, and target tile information. Per-actor and shared chronological logs are written as JSONL files alongside heatmaps in a per-battle session directory.

## Architecture

```
Game (Wine/Proton)                      Linux Native
+-----------------------+                +---------------------------+
| BoamBridge.cs         |   HTTP/JSON   | boam_tactical_engine/     |
|                       | ------------> |                           |
| New hooks:            |  port 7660    | ActionLog.fs (JSONL writer)|
|   Agent.Execute       |               | HookPayload.fs (parsers)  |
|   InvokeOnSkillUse    |               | Program.fs (endpoints)    |
|                       |               |                           |
| Existing hooks:       |               | Battle session dir:       |
|   OnTurnStart         |               |   battle_reports/         |
|   PostProcessTiles    |               |     battle_<timestamp>/   |
|   MovementFinished    |               |       *.jsonl + *.png     |
+-----------------------+               +---------------------------+
```

## Battle Session Directory

All outputs (heatmaps + action logs) are grouped per tactical battle:

```
Mods/BOAM/battle_reports/
  battle_20260313_143022/
    combined_W_stinger_13.png       # heatmap
    actor_7_29_blaster_bug.jsonl    # per-actor log
    actor_3_7_worker.jsonl          # per-actor log
    round_log.jsonl                 # shared chronological log
```

- Created when entering Tactical scene via `POST /hook/battle-start`
- Ended when leaving Tactical via `POST /hook/battle-end`
- Heatmaps that previously went to `Mods/BOAM/heatmaps/` now go here
- Falls back to `Mods/BOAM/heatmaps/` if no active battle session

## Hook Points

### 1. `Agent.Execute` (Prefix) — NEW

- **When:** Before each AI behavior execution (after PickBehavior selected the winner)
- **Threading:** Sequential per agent — safe for HTTP
- **Data sent:** round, faction, actorId, actorName, chosen behavior (ID + name + score), target tile (if Move or SkillBehavior), all alternatives with scores
- **Endpoint:** `POST /hook/action-decision`

**Why Execute prefix and not PickBehavior postfix:**
`Execute()` is public and runs after `PickBehavior()` has set `m_ActiveBehavior`. By this point all behaviors have been evaluated and scored. An agent may Execute multiple times per turn (up to 16 iterations), so each call = one decision.

### 2. `TacticalManager.InvokeOnSkillUse` (Postfix) — NEW

- **When:** After any actor uses a skill (attack, ability)
- **Threading:** Main thread — sequential
- **Data sent:** round, faction, actorId, actorName, skillName (via `GetTitle()`), target tile
- **Endpoint:** `POST /hook/player-action` (filtered to player factions 1, 2 only)
- **Note:** AI skill usage is already captured by the Agent.Execute hook

### 3. `TacticalManager.InvokeOnMovementFinished` (Extended)

- **Extended** to also log player movement as `POST /hook/player-action` when actor is faction 1 or 2
- Original heatmap stamping behavior unchanged

### 4. Battle Session Management

- `OnSceneLoaded("Tactical")` → sends `POST /hook/battle-start` with timestamp
- `OnSceneLoaded(other)` when leaving tactical → sends `POST /hook/battle-end`
- Both sent on background threads via `ThreadPool.QueueUserWorkItem`

## JSONL Schema

### AI Decision (`type: "ai_decision"`)

```json
{
  "round": 1,
  "faction": 7,
  "actorId": 29,
  "actor": "enemy.alien_big_blaster_bug",
  "type": "ai_decision",
  "chosen": {
    "behaviorId": 4,
    "name": "Move",
    "score": 24
  },
  "target": {
    "x": 34,
    "z": 33,
    "apCost": 96
  },
  "alternatives": [
    { "behaviorId": 4, "name": "Move", "score": 24 },
    { "behaviorId": 99999, "name": "Idle", "score": 1 },
    { "behaviorId": 1, "name": "InflictDamage/active.launch_bug_blaster", "score": 0 },
    { "behaviorId": 1, "name": "InflictDamage/active.alien_stab_attack", "score": 0 }
  ]
}
```

### Player Action (`type: "player_move"` or `"player_skill"`)

```json
{
  "round": 1,
  "faction": 1,
  "actorId": 5,
  "actor": "player.soldier_heavy",
  "type": "player_move",
  "skill": "",
  "tile": { "x": 12, "z": 8 }
}
```

## Behavior ID Reference

| ID | Name | Base Score |
|----|------|-----------|
| 1 | InflictDamage | - |
| 2 | InflictSuppression | - |
| 3 | Stun | - |
| 4 | Move | - |
| 5 | Deploy | - |
| 6 | Scan | 50 |
| 7 | Reload | 100 |
| 8 | TurnArmorTowardsThreat | - |
| 9 | TransportEntity | 300 |
| 10 | RemoveStatusEffect | - |
| 11 | Buff | - |
| 12 | CreateLOSBlocker | - |
| 13 | SupplyAmmo | - |
| 14 | TargetDesignator | - |
| 15 | SpawnHovermine | - |
| 16 | SpawnPhantom | - |
| 17 | Mindray | - |
| 18 | GainBonusTurn | - |
| 19 | MovementSkill | - |
| 99999 | Idle | 1 |

## Target Extraction

For the chosen behavior, the bridge extracts target tile information:

- **Move behavior:** `TryCast<Move>()` → `GetTargetTile()` → `Tile.GetX()`, `Tile.GetZ()`, `TileScore.APCost`
- **SkillBehavior (attacks etc.):** `TryCast<SkillBehavior>()` → `m_TargetTile.GetX()`, `m_TargetTile.GetZ()`
- **Other/Idle:** `target` is `null`

## Bug Fixes During Development

### IL Compile Error on Harmony Patch (InvokeOnSkillUse)

**Problem:** `PatchAll()` failed with "IL Compile Error (unknown location)" when adding `Patch_OnSkillUse`.

**Root cause:** Harmony's IL rewriting requires patch method parameters to exactly match the Il2Cpp-wrapped types AND the original parameter names from the target method. The initial patch used:
```csharp
// BROKEN — generic types + wrong parameter name
static void Postfix(object __instance, Actor _actor, Skill _skill, Il2CppMenace.Tactical.Tile _tile)
```

**Fix:** Fully qualify all parameter types and match original parameter names exactly:
```csharp
// WORKING — fully qualified types + exact parameter names from stub
static void Postfix(
    Il2CppMenace.Tactical.TacticalManager __instance,
    Actor _actor,
    Il2CppMenace.Tactical.Skills.Skill _skill,
    Il2CppMenace.Tactical.Tile _targetTile)
```

**Key lessons:**
1. `__instance` must be the concrete Il2Cpp type, not `object`
2. All parameter types must be fully qualified Il2Cpp types — `using` imports may cause ambiguity (e.g., `Skill` could resolve to wrong namespace)
3. Parameter names must match the original method signature from the Il2Cpp stubs (e.g., `_targetTile` not `_tile`)
4. The `Agent.Execute` patch worked with simpler types because `Agent` has no parameter ambiguity — it takes no parameters

**Debugging technique:** Disable all new patches, re-enable one at a time to isolate which causes the IL error. `PatchAll()` applies all `[HarmonyPatch]` classes in the assembly — if any one fails, none work.

### Skill.GetName() doesn't exist

**Problem:** Compile error — `Skill` has no `GetName()` method.
**Fix:** Use `GetTitle()` instead (returns the localized skill display name). Found by reading the extracted stub at `Assembly-CSharp/Menace/Tactical/Skills/Skill.cs`.

### SkillBehavior ambiguity

**Problem:** Compile error — `SkillBehavior` is ambiguous between `Il2CppMenace.Tactical.AI.SkillBehavior` and `Il2CppMenace.Tactical.AI.Data.SkillBehavior`.
**Fix:** Fully qualify as `Il2CppMenace.Tactical.AI.SkillBehavior`.

## Files Changed

| File | Change |
|------|--------|
| `GameTypes.fs` | Added `BehaviorChoice`, `ActionTarget`, `ActionDecisionPayload`, `PlayerActionPayload`, `BattleStartPayload` |
| `ActionLog.fs` | **New** — battle session management, JSONL writer for per-actor + shared logs |
| `HookPayload.fs` | Added `parseActionDecision`, `parsePlayerAction`, `parseBattleStart` |
| `Program.fs` | Added `/hook/battle-start`, `/hook/battle-end`, `/hook/action-decision`, `/hook/player-action` endpoints; heatmap output uses battle session dir |
| `Sidecar.fsproj` | Added `ActionLog.fs` to compile order |
| `BoamBridge.cs` | Added `Patch_AgentExecute` (Agent.Execute prefix), `Patch_OnSkillUse` (InvokeOnSkillUse postfix), battle session start/end in `OnSceneLoaded`, player move logging in `Patch_MovementFinished` |

## Compile Order

```
Config.fs
GameTypes.fs
FactionTheme.fs
Naming.fs
StateKey.fs
StateStore.fs
NodeContext.fs
Node.fs
Registry.fs
Walker.fs
Rendering.fs
ActionLog.fs       <-- NEW
HookPayload.fs
HeatmapRenderer.fs
Program.fs
```

## Next Steps

- [ ] Player action replay via dev console commands (add `attack x z` console command)
- [ ] Capture opponent-based behavior evaluations (InflictDamage target candidates, hit chance, expected damage)
- [ ] Per-criterion breakdown for Move decisions (utility/safety/distance scores on target tile)
- [ ] Round boundary markers in the shared log (round start/end, faction turn start/end)
