# Sidecar Architecture: F# Graph Engine over IPC

## Motivation

FSharp.Core.dll cannot load under Wine/Proton's Windows CoreCLR 6.0.36 — see `step_01_fsharp_failure.md` for the full post-mortem. Every loading strategy (12 attempts) fails with `BadImageFormatException` at the metadata level. This is a Wine CLR bug, not something we can work around.

Rather than abandoning F#, we split BOAM into two processes:

1. **C# bridge plugin** (`src/`) — runs inside MelonLoader under Wine, handles Harmony patches, game state queries, and score injection
2. **F# tactical engine** (`boam_tactical_engine/`) — runs as a native Linux .NET 10 process, owns the behavior graph engine, node evaluation, heatmap rendering, and trace/export
3. **Asset pipeline** (`boam_asset_pipeline/`) — config-driven icon generation from game badge art

The plugin forwards game events and state snapshots to the sidecar; the sidecar evaluates the graph and returns score modifications and commands back to the plugin.

## Why This Is Better Than Just Using C#

The sidecar approach was born from a constraint, but it has genuine advantages:

- **Game-agnostic**: The F# graph engine has zero dependency on MelonLoader, Unity, Il2Cpp, or any game assembly. A different game plugin (BepInEx, SMAPI, raw DLL injection) could connect to the same sidecar.
- **Hot-reload**: The sidecar can restart independently without restarting the game. Graph definitions, node logic, and state key schemas can be iterated without a full game cycle.
- **Native .NET performance**: The sidecar runs on the real Linux .NET runtime (net8.0+), not Wine's CLR. Full access to modern F# features, no Wine compatibility concerns.
- **Crash isolation**: A bug in graph evaluation doesn't crash the game — the plugin detects the sidecar going away and degrades gracefully (AI reverts to vanilla behavior).
- **Testing**: The sidecar can be tested with mock game state, no game required. Integration tests feed recorded state snapshots through the graph and assert on outputs.

## Performance Analysis

The key question: does IPC add unacceptable latency to the AI evaluation loop?

### Menace AI timing

- AI turn takes **5-10 seconds** for a full round (15+ wildlife units)
- Each unit evaluates ~50-200 tiles
- `OnTurnStart` fires per unit (not per faction)
- `ConsiderZones` fires per tile per unit

### IPC overhead

- Localhost TCP or Unix domain socket: **~0.05-0.1ms** per round-trip
- JSON serialization of game state snapshot: **~0.5ms** for full faction state
- Graph evaluation in the sidecar: **<1ms** per unit (pure computation, no game calls)

### Per-round budget

| Phase | Calls | Latency | Total |
|-------|-------|---------|-------|
| OnTurnStart → sidecar (faction state snapshot) | 1 per faction | ~0.5ms | ~0.5ms |
| Sidecar → plugin (ghost positions, score bonuses) | 1 per faction | ~0.2ms | ~0.2ms |
| ConsiderZones → sidecar (tile batch query) | 1 per unit | ~0.3ms | ~4.5ms (15 units) |
| Sidecar → plugin (tile score modifiers) | 1 per unit | ~0.2ms | ~3ms (15 units) |
| **Total IPC overhead** | | | **~8ms** |

**8ms against a 5-10 second AI turn = <0.2% overhead.** Not measurable by the player.

### Optimization: batch protocol

Rather than per-tile IPC, the plugin sends a batch of tile coordinates per unit and receives all score modifiers in one response. This reduces round-trips from O(tiles) to O(units).

## Wire Protocol

### Transport

**HTTP over TCP loopback** on `127.0.0.1:7660` — the same pattern as the game bridge on port 7655.

The initial design used length-prefixed TCP with a custom framing protocol. This was replaced with standard HTTP request/response after recognizing that:
1. The game bridge already proves HTTP loopback between Wine and Linux works
2. Harmony prefix patches provide natural **checkpoints** — the game blocks while waiting for the HTTP response, so no async coordination is needed
3. Standard HTTP eliminates custom framing code and makes the sidecar testable with `curl`

See `step_02_http_checkpoint.md` for the full rationale.

### Endpoints

```
POST /hook/on-turn-start     → { ghosts, scoreModifiers }
POST /hook/consider-zones    → { tileModifiers }
POST /hook/round-end         → { traceExport } (optional)
GET  /status                 → { version, graphs, uptime }
POST /shutdown               → sidecar exits gracefully
```

Each hook endpoint receives game state as JSON in the request body, evaluates the behavior graph, and returns modifications in the response body. HTTP status codes: 200 = modifications applied, 204 = no changes, 500 = error (plugin lets AI run vanilla).

### Checkpoint Pattern

Harmony prefix patches make **blocking HTTP calls** to the sidecar. The game's AI evaluation is paused inside the prefix while the sidecar processes the graph. When the HTTP response arrives, the plugin applies the modifications and the prefix returns, allowing the game to continue.

This is safe because AI evaluation already takes 5-10 seconds — a sub-millisecond HTTP call is invisible. If the sidecar is down, the request fails immediately and the game continues unmodified.

### Lifecycle

1. Sidecar starts (manually, via deploy script, or via launcher wrapper)
2. Game loads → plugin exists but makes no connections
3. Harmony patches fire → plugin POSTs to sidecar → sidecar responds → plugin applies result
4. Game exits → plugin POSTs `/shutdown` as a courtesy
5. Sidecar can stay running for hot-reload across game restarts

## Architecture Diagram

```
┌──────────────────────────────────────────────────┐
│  Game Process (Wine/Proton)                      │
│                                                  │
│  ┌──────────────────────────────────────────┐    │
│  │  MelonLoader (.NET 6.0 under Wine)       │    │
│  │                                          │    │
│  │  ┌──────────────────────────────────┐    │    │
│  │  │  Game Bridge (HTTP :7655)        │◄───┼────┼──── Sidecar queries
│  │  └──────────────────────────────────┘    │    │     extra state
│  │                                          │    │
│  │  ┌──────────────────────────────────┐    │    │
│  │  │  BOAM C# Plugin (thin shim)     │    │    │
│  │  │  - Harmony prefix patches        │    │    │
│  │  │  - State extraction → JSON       │    │    │
│  │  │  - HTTP POST to sidecar (blocks) │────┼────┼──── POST /hook/on-turn-start
│  │  │  - Apply response modifications  │◄───┼────┼──── { ghosts, scoreModifiers }
│  │  └──────────────────────────────────┘    │    │
│  └──────────────────────────────────────────┘    │
└──────────────────────────────────────────────────┘
                                                   │
               HTTP loopback 127.0.0.1             │
                                                   │
┌──────────────────────────────────────────────────┐
│  F# Sidecar (native Linux .NET 10.0)            │
│  HTTP server on :7660                            │
│                                                  │
│  ┌──────────────────────────────────────────┐    │
│  │  ASP.NET Minimal API                     │    │
│  │  POST /hook/on-turn-start                │    │
│  │  POST /hook/consider-zones               │    │
│  │  GET  /status                            │    │
│  └──────────────┬───────────────────────────┘    │
│  ┌──────────────┴───────────────────────────┐    │
│  │  Graph Engine                            │    │
│  │  - Node registry & merge                 │    │
│  │  - State key store                       │    │
│  │  - Walker (per-hook execution)           │    │
│  │  - Trace capture                         │    │
│  └──────────────┬───────────────────────────┘    │
│  ┌──────────────┴───────────────────────────┐    │
│  │  Behavior Graphs (mod-defined)           │    │
│  │  - BooAPeek ghost nodes                  │    │
│  │  - Potshot speculative fire              │    │
│  │  - etc.                                  │    │
│  └──────────────────────────────────────────┘    │
│  ┌──────────────────────────────────────────┐    │
│  │  Export (CSV / PNG heatmaps)             │    │
│  └──────────────────────────────────────────┘    │
└──────────────────────────────────────────────────┘
```

## C# Plugin Responsibilities

The plugin is intentionally thin — it handles only what requires running inside the game process:

1. **Harmony patches**: Register prefix patches on `AIFaction.OnTurnStart`, `ConsiderZones`, etc.
2. **State extraction**: Read faction data, opponent lists, unit positions from Il2Cpp objects and serialize to JSON
3. **Blocking HTTP POST**: Call sidecar endpoints from within Harmony prefixes — game waits for response
4. **Apply modifications**: Inject ghost commands, score modifiers returned by the sidecar
5. **Graceful degradation**: If sidecar is down, catch the HTTP exception, log a warning, let AI run vanilla

The plugin does NOT:
- Manage a persistent connection (stateless HTTP)
- Define behavior graphs or nodes
- Evaluate graph logic
- Track cross-hook state
- Generate traces or exports

## F# Sidecar Responsibilities

1. **HTTP server**: ASP.NET minimal API on port 7660 with one endpoint per game hook
2. **Graph engine**: Registry, merge, validation, walker — all the core BOAM logic from `DESIGN.md`
3. **Node evaluation**: Run registered nodes per hook, manage state keys
4. **Bridge queries**: Optionally query game bridge (port 7655) for additional state during hook processing
5. **Trace/export**: Capture per-node execution traces, generate CSVs/PNGs
6. **Hot-reload**: Restart independently of the game — graph changes take effect on next hook call

## Implementation Plan (Revised Step 1)

### Step 1a: F# sidecar as HTTP server

- F# console project: `BOAM-modpack/sidecar/`, targets `net10.0`
- ASP.NET minimal API on `http://127.0.0.1:7660`
- `GET /status` returns version and readiness
- `POST /hook/on-turn-start` receives faction state, returns hardcoded echo for now
- Testable with `curl`

### Step 1b: C# plugin with HTTP client

- `BOAM-modpack/src/BoamBridge.cs` implements `IModpackPlugin`
- On `OnSceneLoaded("Title")`: check sidecar is up via `GET /status`
- No Harmony patches yet — just prove HTTP round-trip works inside Wine
- Log sidecar version and status

### Step 1c: End-to-end test in game

- Start sidecar, launch game, observe plugin connecting to sidecar in logs
- Test with `curl` from Linux side simultaneously
- Measure round-trip latency

### Success criteria

- Sidecar starts, responds to `curl` and to the in-game plugin
- HTTP round-trip from Wine → Linux loopback works reliably
- Plugin degrades gracefully when sidecar is absent (timeout → warning → vanilla AI)
- Latency < 5ms per request

## Future: Game-Agnostic BOAM

The sidecar architecture opens the door to supporting multiple games:

- The wire protocol is game-agnostic — `FactionState`, `TileBatch`, `ScoreModifiers` describe abstract tactical concepts
- A new game needs only a thin plugin that extracts its state into the protocol format
- The same behavior graphs work across games if the state keys map to equivalent concepts
- Game-specific nodes can coexist with game-agnostic nodes in the same graph

This is a long-term vision, not a v1 requirement. But the architecture supports it from day one.

## Related Documents

| Document | Content |
|----------|---------|
| `step_02_http_checkpoint.md` | Why HTTP checkpoints replaced the TCP protocol — full rationale |
| `step_01_fsharp_failure.md` | Why FSharp.Core can't load under Wine — 12 attempts documented |
| `step_01_fsharp_proof.md` | Original F# compilation proof (BOAM.dll loads, FSharp.Core doesn't) |
| `DESIGN.md` | BOAM graph engine design (unchanged by transport choice) |
| `IMPLEMENTATION_PLAN.md` | Original 10-step plan (needs update for sidecar) |
| `v1_minimal_spec.md` | v1 scope, API, trace format |
