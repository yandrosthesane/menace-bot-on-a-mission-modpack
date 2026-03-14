# Step 09 — Battle Replay

## Feature

Record all player actions during a tactical battle, then replay them automatically through the game bridge to reproduce the exact same battle.

## Architecture

```
round_log.jsonl ──> Replay.fs ──> Game Bridge (7655)
                    (port 7660)     ├── select <id>
                                    ├── move <x> <z>
                                    ├── useskill <name> <x> <z>
                                    └── endturn
```

**Recording** happens automatically — the C# bridge plugin sends `player_move` and `player_skill` events to the tactical engine, which writes them to `round_log.jsonl` via ActionLog.

**Replay** reads the JSONL, filters to `player_*` entries, and drives them back through the game bridge as console commands.

## Synchronization Problem

The game runs in real-time with animations and AI turns. A naive "send all commands as fast as possible" approach fails because:

1. **Move/skill animations** take 1–3 seconds — sending the next command before the animation finishes either queues or drops it
2. **AI turns** happen between rounds and take 5–30s depending on unit count — the replay must wait for all AI factions to finish before sending round N+1 player actions
3. **Turn order** — each player unit must have its turn ended (`endturn`) after its action before the next unit can act

## Solution

Two-tier synchronization:

### Within a round: fixed delay

Between player actions, wait `delayMs` (configurable, default 3000ms). This covers animation time. Sequence per action:

```
select <actorId>     → 500ms wait (selection takes effect)
move/useskill        → delayMs wait (animation plays)
endturn              → 500ms wait (turn advances to next unit)
```

### Between rounds: poll game state

After the last player action in a round, poll `GET /tactical` on the game bridge every 1 second until:

- `isPlayerTurn == true` (AI factions have finished)
- `round >= targetRound` (the round counter has advanced)

Timeout: 90 seconds. If exceeded, replay continues anyway (logs a warning).

The `/tactical` endpoint returns:
```json
{
  "round": 2,
  "isPlayerTurn": true,
  "activeActor": "Rewa",
  "faction": 1,
  "factionName": "Player",
  ...
}
```

## API

### `GET /replay/battles`

List all recorded battles with round/action counts.

### `GET /replay/actions/{battleName}`

View all player actions from a specific battle.

### `POST /replay/run`

Start a replay. Blocks until complete.

```json
{
  "battle": "battle_20260313_214741",
  "round": 1,
  "delayMs": 3000
}
```

- `battle` (required) — directory name under `battle_reports/`
- `round` (optional) — replay only this round; omit for all rounds
- `delayMs` (optional, default 3000) — pause between player actions in ms

Returns:
```json
{
  "battle": "battle_20260313_214741",
  "round": null,
  "total": 12,
  "succeeded": 12,
  "failed": 0,
  "log": ["── Round 1: 4 player actions ──", "Rewa player_move -> (5,3): ...", ...]
}
```

## Open Questions

1. **endturn for units with no recorded action** — if the original battle had a player unit that did nothing (skipped turn), we don't record that. The replay sends `endturn` after each action, but units without recorded actions won't get their turn ended. This may cause the turn to stall waiting for player input on a unit that has no replay action. Possible fix: after replaying all actions for a round, send `endturn` for any remaining player units that weren't in the action log.

2. **Multi-action units** — if a unit performs multiple actions per turn (e.g., move then shoot), the current flow sends `endturn` after each action. This would cut the unit's turn short after the first action. Fix: group actions by actorId within a round and only `endturn` after the last action for each unit.

3. **Action point consumption** — moves consume AP. If the replayed move path differs slightly (pathfinding non-determinism?), the unit might run out of AP before reaching the target tile. The `move` command returns "MoveTo returned false" in this case — should the replay skip to the next action or abort?

4. **Save seed determinism** — we assume loading the same save produces the same game seed, so AI decisions are identical. This needs verification — are seeds tied to the save or generated fresh each mission start?

5. **delayMs tuning** — 3000ms is conservative. Some actions (single-tile moves) complete in <1s, while long-range moves or skill animations may need more. Could we poll the actor's `isMoving` state instead of using a fixed delay? The `/actor` endpoint exposes `movement.isMoving`.

## Files

| File | Role |
|------|------|
| `boam_tactical_engine/Replay.fs` | Replay engine — parse JSONL, execute actions, sync between rounds |
| `boam_tactical_engine/Program.fs` | HTTP endpoints: `/replay/battles`, `/replay/actions/{name}`, `/replay/run` |
| `boam_tactical_engine/ActionLog.fs` | Records player actions to JSONL during live play |
| `src/BoamBridge.cs` | C# bridge hooks that capture player moves/skills |
| `MenaceAssetPacker/.../DevConsole.cs` | `select`, `move`, `useskill`, `endturn` console commands |
