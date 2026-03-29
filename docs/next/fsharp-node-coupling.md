# F# Node Coupling — Refactor Spec

## Problem

Adding a behaviour node + its data source currently touches 6+ F# files:

| File | Change | Should be needed? |
|------|--------|-------------------|
| New node `.fs` | Node logic | Yes |
| `behaviour.json5` | Hook chain + preset | Yes |
| `GameTypes.fs` | State type definition | No — node-specific types should live in the node file |
| `Keys.fs` | State key definition | No — keys should live where they're used |
| `HookHandlers.fs` | Hook handler for incoming events | No — should be auto-registered or declared in the event/node |
| `Program.fs` | Catalogue registration | No — should be auto-discovered |
| `TacticalEngine.fsproj` | Compile order | Unavoidable in F# (file ordering matters) |

The investigate behaviour is a clear example: the node itself is simple (read targets, score tiles). But wiring it required edits across the entire engine.

## Tensions

### 1. Types live in GameTypes.fs

Every new state shape (InvestigateTarget, ActorPosState, etc.) goes into the central `GameTypes.fs`. This couples all nodes to one file and forces recompilation of everything downstream.

**Target:** Node-specific types live in the node file or a companion file. Only shared types (TilePos, TileModifier, FactionState) stay in GameTypes.fs.

### 2. State keys live in Keys.fs

Every new state key goes into `Keys.fs`. This is a central registry that every node imports.

**Target:** Keys are declared where they're produced or consumed. A node that owns a state key declares it in its own module. Shared keys (tile-modifiers, actor-positions) stay in Keys.fs.

### 3. Hook handlers are manually registered in HookHandlers.fs

Every new event type from C# needs a handler added to `HookHandlers.fs` and registered in the dispatch table. This file grows with every feature.

**Target:** Hook handlers are auto-registered. Each handler declares its hook name and is discovered at startup — same pattern as C# game events with `Register()`.

### 4. Nodes are manually registered in Program.fs

Every node needs `Catalogue.register` in Program.fs. Adding a node means editing the entry point.

**Target:** Auto-discovery. The catalogue scans the assembly for types matching the NodeDef contract, or nodes register themselves via a module init.

### 5. F# file ordering in fsproj

F# requires explicit compile order. Every new file needs a `<Compile>` entry in the right position. This is a language constraint, not a design issue.

**Mitigation:** Not avoidable, but grouping related files (node + its types + its keys) in the fsproj makes it clearer.

## Ideal: adding a new node

1. Create `Nodes/NewBehaviour.fs` — contains the type, state key, and node definition
2. Add to `behaviour.json5` hook chain
3. Add `<Compile>` to fsproj
4. If the node consumes data from a C# game event, add the handler in the node file or a companion

No edits to GameTypes.fs, Keys.fs, HookHandlers.fs, or Program.fs.

## Ideal: adding a new C# → F# event

1. Create `src/GameEvents/NewEvent.cs` — C# side
2. The F# handler is either:
   a. In the node file that consumes the data, OR
   b. Auto-registered from a companion handler file

No edits to HookHandlers.fs dispatch table.

## Practical first step: self-registering modules

F# modules can run init code at module load time. If each node module calls `Catalogue.register` on itself during init, Program.fs doesn't need to list every node. Similarly, a hook handler module could register its dispatch entry, and types/keys could live alongside the node.

A single node file could contain:

```fsharp
module BOAM.TacticalEngine.Nodes.InvestigateBehaviour

// Types (node-specific, not in GameTypes.fs)
type InvestigateTarget = { Position: TilePos; Faction: int; RoundCreated: int }

// State key (not in Keys.fs)
let investigateTargets = StateKey.perSession<InvestigateTarget list> "investigate-targets"

// Config preset (not in Config.fs)
// ... read from behaviour.json5 at module init

// Hook handler (not in HookHandlers.fs)
// ... registers itself in the dispatch table at module init

// Node definition
let node : NodeDef = { ... }

// Self-registration
do Catalogue.register node
```

This mirrors how C# game events are self-contained. The fsproj compile order entry is unavoidable in F# but is the only external change needed.

## C# side: unified GameStore

Event state has been moved to `Boundary/GameStore.cs` (untyped `Dictionary<string, object>`, cleared on battle end). Remaining state outside the store:

| Current location | Data | Why not in GameStore yet |
|-----------------|------|--------------------------|
| `TileModifierStore._store` | Per-actor tile modifiers | Data should move to GameStore. `ManualResetEventSlim` (wait/signal) is coordination — stays in the event. |
| `TileModifierStore._ready` | ManualResetEventSlim | Coordination, not data. Stays in event. |
| `TacticalMapState` (all fields) | Units, map texture, tiles, round, active actor, battle/preview dirs | Data should move to GameStore. `_unitsLock` + snapshot cache is thread-safe access — stays in minimap renderer. |
| `ActionLoggingEvent.SkillAnimationEndTime` | Command gate timer | Should move to GameStore. |
| ~~`ActorRegistry`~~ | ~~Entity ID ↔ UUID mappings~~ | Done — moved to GameStore. |

The pattern: stores should hold data. Synchronization and access patterns belong to the events/systems that use the data.

## F# side: scattered state

The F# `StateStore` is the shared store for node data. But some state lives outside it:

| Location | Data | Move to StateStore? |
|----------|------|---------------------|
| `HookHandlers.currentRound` | Mutable round counter | Yes — quick win |
| `ActionLog.currentBattleDir` | Mutable battle directory path | Yes — quick win |
| `ActionLog` file handles | JSONL writers, I/O resources | No — not data |
| `RenderJobCollector` | Accumulated render jobs | No — flush logic tied to the structure |
| `MessagingClient` | HttpClient + URL | No — infrastructure |
| `EventBus` | Event queue | No — coordination mechanism |

## Status

Documented. The current system works but doesn't scale cleanly. Each tension has a path forward — the question is priority and implementation order.
