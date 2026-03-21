# BOAM Replay System

The replay system records every player action during a tactical mission and plays them back automatically, reproducing the exact sequence of moves, skills, and attacks. An optional determinism watchdog compares AI decisions during replay against the original recording.

## Prerequisites

Replay requires action logging to be enabled in `config.json5`:

```json5
"action_logging": true,  // required for replay
"ai_logging": true,      // optional — enables determinism watchdog
```

See [Configuration](README_CONFIG.md) for details.

## Recording

Recording is automatic when `action_logging` is enabled. When you enter a tactical mission with BOAM active and the tactical engine running, all player actions (and AI decisions if `ai_logging` is on) are captured in `round_log.jsonl`. Battle names are auto-generated as `battle_YYYY_MM_DD_HH_MM`.

**Note:** Avoid selecting actors by clicking their tile during recording. Use the portrait bar instead — tile-click selection produces logging artifacts that can stall replay.

## Replay

### CLI (recommended)

Start the tactical engine with a replay target, then launch the game. The engine handles navigation and starts the replay automatically.

**Linux:**
```bash
./start-tactical-engine.sh --on-title /navigate/replay/battle_2026_03_15_15_14
```

**Windows:**
```bat
start-tactical-engine.bat --on-title /navigate/replay/battle_2026_03_15_15_14
```

Camera and determinism options are passed as query parameters:

**Linux:**
```bash
# Free camera (no auto-follow)
./start-tactical-engine.sh --on-title "/navigate/replay/battle_2026_03_15_15_14?camera=free"

# Determinism watchdog — halt on first AI divergence
./start-tactical-engine.sh --on-title "/navigate/replay/battle_2026_03_15_15_14?determinism=stop"

# Determinism watchdog — log all divergences, don't halt
./start-tactical-engine.sh --on-title "/navigate/replay/battle_2026_03_15_15_14?determinism=log"

# Combine options
./start-tactical-engine.sh --on-title "/navigate/replay/battle_2026_03_15_15_14?camera=free&determinism=stop"
```

**Windows:**
```bat
REM Free camera
start-tactical-engine.bat --on-title "/navigate/replay/battle_2026_03_15_15_14?camera=free"

REM Determinism watchdog — halt on first divergence
start-tactical-engine.bat --on-title "/navigate/replay/battle_2026_03_15_15_14?determinism=stop"
```

Then launch the game normally through Steam.

### HTTP (manual control)

All endpoints are on the tactical engine at `http://127.0.0.1:7660`.

**List recorded battles:**
```bash
curl -s http://127.0.0.1:7660/replay/battles
```

**Start a replay** (game must be in Tactical):
```bash
curl -s -X POST http://127.0.0.1:7660/replay/start \
  -d '{"battle": "battle_2026_03_15_15_14", "camera": "follow", "determinism": "stop"}'
```

**Navigate to tactical and start replay** (game must be on Title):
```bash
curl -s -X POST http://127.0.0.1:7660/navigate/replay/battle_2026_03_15_15_14?determinism=stop
```

**Check divergences during replay:**
```bash
curl -s http://127.0.0.1:7660/replay/divergences
```

**Stop an active replay** (includes divergence report):
```bash
curl -s -X POST http://127.0.0.1:7660/replay/stop
```

## Camera Modes

- **follow** (default) — camera tracks the active actor, centering on each unit as it takes its turn.
- **free** — camera stays where you left it. Use this when you want to watch a specific area of the map.

## Determinism Watchdog

When `ai_logging` is enabled and a battle is recorded with AI decisions, the replay engine can compare each AI decision during replay against the original recording.

**Modes** (set via `determinism` parameter):
- **off** (default) — no comparison
- **log** — record all divergences, replay continues to completion
- **stop** — halt replay at the first divergence

**What a divergence tells you:**
- Which AI actor made a different decision
- What was expected (behavior, score, target tile) vs what actually happened
- The last player action before the divergence

This is useful for:
- Verifying replay fidelity
- Measuring the impact of AI behavior modifications — replay the same battle with modified AI and see exactly where decisions change

**Endpoints:**
- `GET /replay/divergences` — query divergences mid-replay
- `POST /replay/stop` — response includes full divergence list

## Battle Report Storage

Battle reports are stored persistently in:

```
UserData/BOAM/battle_reports/<battle_name>/
├── round_log.jsonl          Player actions + AI decisions
├── dramatis_personae.json   Actor registry
├── mapbg.png                Captured map background
├── mapbg.info               Tile dimensions
├── mapdata.bin              Binary tile data
├── render_jobs/             Heatmap render job data
└── heatmaps/                Rendered heatmap PNGs
```

Reports persist across game restarts and mod deploys.
