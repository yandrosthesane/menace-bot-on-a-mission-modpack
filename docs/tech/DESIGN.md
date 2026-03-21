# BOAM — Bot On A Mission

## Goal

A registration framework for AI behaviour modifications. Instead of writing ad-hoc Harmony patches per mod, BOAM provides a structured way to register intercepts, filters, and injections against the game's AI decision systems.

## Problem

BooAPeek proved a repeatable pattern for modifying AI behaviour:

1. **Identify** which system misbehaves (AI knows about concealed units it never spotted)
2. **Intercept** at the minimal root function (`AIFaction.OnTurnStart` — where the opponent list is read)
3. **Filter** the input data (strip opponents the AI hasn't seen via LOS checks)
4. **Inject** a response (create ghosts at last-seen positions, feed utility score bonuses)

This works, but each mod re-implements the same plumbing:
- Harmony patch registration and lifecycle
- Faction discovery and state tracking
- Event timing (per-unit vs per-round vs per-tile)
- Score injection into the right evaluation function

Every new AI modification requires a new mod with its own patches, its own state management, and its own understanding of the AI evaluation pipeline. The patterns are identical — only the *what to filter* and *what to inject* differ.

## Chosen Architecture: Merged Data-Driven Behavior Graph

After evaluating four candidates (event bus, pipeline, behavior graph, typed dataflow) against three concrete mods (BooAPeek, Potshot, I'm Under Fire), we chose a **merged data-driven behavior graph** — a hybrid that combines the behavior graph's data-driven node model with typed state keys for cross-hook/cross-mod dependency tracking.

See `candidate_evaluation.md` for the full comparison and `candidate_merged_behavior_graph.md` for the detailed design.

### Core concepts

- **Nodes** are the unit of work. Each node has a name, a hook binding (when it fires), declared reads/writes (what state it touches), and a run function.
- **State keys** are typed, named stores that persist across hooks. A node declares which keys it reads and writes. The framework validates these at registration time.
- **Merge** happens at registration: all nodes from all installed mods are collected, grouped by hook, and validated. Missing writers, write conflicts, and dependency chains are all detected before the game starts.
- **Walker** executes nodes in declaration order per hook. Since the framework owns execution, it can trace every node invocation — what it read, what it wrote, how long it took.

### Why this approach

| Requirement | How it's met |
|-------------|-------------|
| Simple case (one mod, one hook) | Register nodes, declare reads/writes — similar effort to an event handler |
| Cross-hook state (ghosts created in OnTurnStart, read in ConsiderZones) | State keys persist across hooks — explicitly declared, framework-managed |
| Cross-mod dependency (Potshot reads BooAPeek's ghost data) | Potshot declares `reads BooAPeek.Ghost` — if BooAPeek not installed, warning at registration |
| Cross-unit effects (I'm Under Fire alerts nearby allies) | Node declares `writes RoleData.Nearby` — visible, detectable conflicts |
| Debugging | Framework traces every node, captures score diffs, exports CSVs/PNGs |

### Why F#

- **Nodes as records** — `{ Name: string; Hook: HookPoint; Reads: StateKey list; Writes: StateKey list; Run: NodeContext -> unit }` — data, not classes
- **State keys as typed values** — `let ghost: StateKey<GhostMap> = StateKey "BooAPeek.Ghost"` — cross-mod references are just values
- **Pattern matching** for dispatch, discriminated unions for hook/timing enums
- **Pipe operator** for context transforms within node functions
- **C# stays for Harmony patches** — the OO surface (IModpackPlugin, HarmonyPatch attributes) remains in C# or minimal F# classes

## Score Export

When capture is enabled, BOAM dumps per-actor per-round CSVs with tile scores and behavior scores. Optionally renders PNG heatmaps compositable with TacticalMap's terrain output.

The modder workflow: load save → play round → examine CSVs/PNGs → toggle BOAM off → reload same save → play round → manually compare the two sets.

See `v1_minimal_spec.md` for full details on CSV format, PNG rendering, and console commands.

## Implementation Plan

Ten incremental steps, each deployable and testable. See `IMPLEMENTATION_PLAN.md`.

## Runtime Architecture: Sidecar over IPC

The graph engine runs as a **separate native Linux .NET process** (the "sidecar"), not inside MelonLoader. This is required because FSharp.Core.dll cannot load under Wine/Proton's Windows CoreCLR (see `step_01_fsharp_failure.md`).

A thin C# plugin inside MelonLoader registers Harmony prefix patches that make **blocking HTTP calls** to the sidecar at each AI checkpoint. The game waits for the sidecar to evaluate the graph and return modifications before continuing. The sidecar owns the graph engine, node evaluation, merge logic, and trace/export.

This split makes BOAM **game-agnostic** — the sidecar has zero dependency on any game, and different game plugins can connect to the same graph engine.

See `sidecar_architecture.md` for the full design, wire protocol, and performance analysis.

## Related Documents

| Document | Content |
|----------|---------|
| `sidecar_architecture.md` | Sidecar architecture — HTTP checkpoints, performance, implementation plan |
| `step_02_http_checkpoint.md` | Why HTTP checkpoints replaced the TCP protocol |
| `step_01_fsharp_failure.md` | Why FSharp.Core can't load — the constraint that led to the sidecar |
| `step_01_fsharp_proof.md` | Original F# compilation proof |
| `IMPLEMENTATION_PLAN.md` | Step-by-step build plan (10 steps) |
| `v1_minimal_spec.md` | v1 scope, API examples, trace output, score export, success criteria |
| `candidate_evaluation.md` | Four candidates evaluated against three concrete mods |
| `candidate_merged_behavior_graph.md` | Chosen architecture — full design |
| `candidate_event_bus.md` | Event bus candidate (rejected) |
| `candidate_pipeline.md` | Pipeline candidate (rejected) |
| `candidate_behavior_graph.md` | Behavior graph candidate (superseded by merged variant) |
| `candidate_typed_dataflow.md` | Typed dataflow candidate (ideas absorbed into merged variant) |
| `AI_TERMINAL_SCORES.md` | Catalog of every pilotable AI score in the game |
| `BAP_DECOMPOSITION.md` | BooAPeek decomposed into reusable primitives |
