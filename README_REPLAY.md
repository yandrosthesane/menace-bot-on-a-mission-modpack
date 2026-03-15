# BOAM Replay System

The replay system records every player action during a tactical mission and plays them back automatically, reproducing the exact sequence of moves, skills, and attacks.

## Recording

Recording is automatic. When you enter a tactical mission with BOAM active and the tactical engine running, all player actions are captured and stored as a battle report. Battle names are auto-generated as `battle_YYYY_MM_DD_HH_MM`.

## Replay

### CLI (recommended)

Start the tactical engine with a replay target, then launch the game. The engine handles navigation to the mission and starts the replay automatically.

```bash
./start-tactical-engine.sh --on-title /navigate/replay/battle_2026_03_15_15_14
steam -applaunch 2432860
```

To use free camera (you control the camera instead of it tracking the active unit):

```bash
./start-tactical-engine.sh --on-title "/navigate/replay/battle_2026_03_15_15_14?camera=free"
```

### HTTP (manual control)

All endpoints are on the tactical engine at `http://127.0.0.1:7660`.

**List recorded battles:**

```bash
curl -s http://127.0.0.1:7660/replay/battles
```

**Start a replay** (game must be in Tactical):

```bash
curl -s -X POST http://127.0.0.1:7660/replay/start \
  -H "Content-Type: application/json" \
  -d '{"battle": "battle_2026_03_15_15_14", "camera": "follow"}'
```

**Navigate to tactical and start replay** (game must be on Title):

```bash
curl -s -X POST http://127.0.0.1:7660/navigate/replay/battle_2026_03_15_15_14?camera=follow
```

**Stop an active replay:**

```bash
curl -s -X POST http://127.0.0.1:7660/replay/stop
```

## Camera Modes

- **follow** (default) -- camera tracks the active actor, centering on each unit as it takes its turn.
- **free** -- camera stays where you left it. Use this when you want to watch a specific area of the map.

## Battle Report Storage

Battle reports are stored persistently in:

```
UserData/BOAM/battle_reports/<battle_name>/
```

Reports persist across game restarts and mod deploys.

For technical details (architecture, timing, duration measurement), see [docs/README_REPLAY.md](docs/README_REPLAY.md).
