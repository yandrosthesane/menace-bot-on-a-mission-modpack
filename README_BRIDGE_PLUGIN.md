# C# Bridge Plugin (`src/`)

Thin MelonLoader plugin that runs inside the game under Wine/Proton. Hooks into the AI evaluation loop and player actions via Harmony patches, forwarding data to the tactical engine over HTTP.

## Harmony Hooks

| Patch Target | What It Captures |
|-------------|-----------------|
| `AIFaction.OnTurnStart` | Faction state (opponents, actors, round) |
| `Agent.PostProcessTileScores` | Per-tile AI scores + all unit positions |
| `Agent.Execute` | AI behavior decisions (chosen action, alternatives, attack candidates) |
| `TacticalManager.InvokeOnMovementFinished` | Move destinations → `player_move` |
| `TacticalManager.InvokeOnSkillUse` | Skill usage → `player_skill` |
| `TacticalManager.InvokeOnMovement` | Embark (`MovementAction.Enter`) / Disembark (`MovementAction.Leave`) |

## Game Bridge

The plugin starts an HTTP server on port 7655 that exposes:
- `GET /status` — scene, version, timestamp
- `GET /tactical` — round, faction, active actor, player turn status
- `GET /actors` — all actors with positions
- `POST /cmd` — execute DevConsole commands (`move`, `useskill`, `endturn`, etc.)

## Data Flow

```
Game (Wine/Proton)                    Tactical Engine (native Linux/.NET 10)
┌──────────────────┐                  ┌──────────────────────┐
│  BoamBridge.cs   │  HTTP POST       │  Program.fs          │
│  Harmony patches ├─────────────────►│  /hook/on-turn-start │
│                  │  port 7660       │  /hook/tile-scores   │
│                  │                  │  /hook/action-decision│
│                  │                  │  /hook/player-action │
└──────────────────┘                  └──────────────────────┘
```

## Action Logging Details

All player actions are sent to `/hook/player-action` with:
- `round`, `faction`, `actorId`, `actor` (template name)
- `type`: `player_move`, `player_skill`, `player_embark`, `player_disembark`, `player_endturn`
- `tile`: `{x, z}`
- `skill`: skill name (for `player_skill`)
- `vehicleId`: entity ID (for `player_embark`)

### Disembark Dedup

When a unit disembarks, both `InvokeOnMovement` (Leave) and `InvokeOnMovementFinished` fire. The bridge sets `_lastDisembarkActorId` on the Leave event so `Patch_MovementFinished` skips the duplicate `player_move` for that actor.
