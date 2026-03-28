# Replay System Readiness (2026-03-14)

### All DevConsole commands tested and working

| Command | Tested | BOAM Logged | Notes |
|---------|--------|-------------|-------|
| `who` | Yes | N/A | Shows active actor ID, template, pos, contained status |
| `move <x> <z>` | Yes | `player_move` | One tile at a time, pathfinding |
| `useskill "Deploy" <x> <z>` | Yes | `player_skill` | Self-targeting, executes immediately |
| `useskill "Get Up" <x> <z>` | Yes | `player_skill` | Self-targeting, executes immediately |
| `useskill "Vehicle Rotation" <x> <z>` | Yes | `player_skill` | Target = adjacent tile to face toward |
| `embark <vehicleId>` | Yes | Not logged by BOAM | Uses ContainEntity directly, bypasses InvokeOnMovement |
| `disembark <x> <z>` | Yes | Not logged by BOAM | Uses EjectEntity directly, bypasses InvokeOnMovement |
| `endturn` | Yes | `player_endturn` | Progresses to next unit |
| `select <id>` | Unreliable | N/A | Doesn't change game UI's active actor reliably |

### BOAM action logging (Harmony patches in BoamBridge.cs)

- `Patch_SkillUse` — intercepts `TacticalManager.InvokeOnSkillUse`, logs `player_skill` with skill name and tile
- `Patch_MovementFinished` — intercepts `TacticalManager.InvokeOnMovementFinished`, logs `player_move`
- `Patch_Movement` — intercepts `TacticalManager.InvokeOnMovement`, detects `MovementAction.Enter` (embark) and `MovementAction.Leave` (disembark)
- Dedup: `_lastDisembarkActorId` prevents Patch_MovementFinished from logging duplicate `player_move` for disembark

### Manual embark/disembark vs console commands
- **Manual** (game UI): captured by Harmony patches as `player_embark` / `player_disembark`
- **Console** (`embark`/`disembark` commands): NOT captured — uses ContainEntity/EjectEntity directly
- For replay: the replay system uses console commands, so it doesn't need BOAM logging for these

### Replay engine (F# — boam_tactical_engine/Replay.fs)
- Parses JSONL battle log
- Maps action types to console commands: `player_move` → `move`, `player_skill` → `useskill`, `player_embark` → `embark`, `player_disembark` → `disembark`
- Skill names with spaces are quoted: `useskill "%s" %d %d`
- `vehicleId` field extracted for embark actions

### Known issues for next session
1. **Vehicle Rotation logs twice** — duplicate `player_skill` entries in BOAM log. Minor, investigate if it affects replay.
2. **`select` unreliable** — `TacticalController.SetActiveActor()` doesn't reliably change the game's active actor. Use `who` to check and `endturn` to cycle through units.
3. **EntityCombat.UseAbility still broken** — still uses `skill.Use()` which doesn't exist. Low priority.

### Next step: End-to-end replay test
Play a mission manually (move, deploy, shoot, embark, disembark, endturn), then replay it using the tactical engine's `/replay/run` endpoint.
