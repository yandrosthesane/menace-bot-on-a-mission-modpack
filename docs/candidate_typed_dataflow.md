# Candidate: Typed Dataflow Graph

## Model

Nodes are pure functions with typed inputs and outputs. Edges are data connections between nodes. The framework owns the execution: when a hook fires, it walks the graph from entry nodes, passing data along edges.

```
Node: FilterOpponents
  trigger: OnTurnStart.Prefix
  input:   AIFaction × Opponent list
  output:  Opponent list (filtered) × Removed list

Node: RecordSighting
  input:   ActorId × Position × FactionIdx
  output:  state∆ LastSeen

Node: CreateGhost
  input:   ActorId × LastSeenPosition × Settings
  output:  state∆ Ghost

Edge: FilterOpponents.Removed → CreateGhost.input
Edge: FilterOpponents.Visible → RecordSighting.input
```

State is a first-class concept: State nodes are named stores. Commands write to them, Queries read from them. The graph declares these dependencies explicitly.

## Execution model

```
Game hook fires
  → BOAM identifies all entry nodes for this hook
  → Topologically sorts downstream nodes
  → Executes in dependency order
  → Each node receives its inputs, produces outputs
  → Outputs are routed to connected downstream nodes
  → State nodes accumulate mutations
  → Terminal nodes apply results to game data
```

## BAP expressed as typed dataflow

```
Entry:OnTurnStart.Prefix
  ├─► [FilterOpponents]  (AIFaction, Opponents) → (Visible, Removed)
  │     ├─► [RecordSighting]  (Visible) → ∆LastSeen
  │     └─► [RecordLOSLost]   (Removed) → ∆Ghost
  └─► (done — game AI sees filtered opponents)

Entry:OnTurnStart.Postfix
  ├─► [DecayGhosts]      (Ghosts) → (Ghosts', Expired)
  ├─► [ComputeWaypoints] (Ghosts', FactionActors) → Ghosts''
  └─► [ExpireGhosts]     (Expired) → ∆Ghost

Entry:OnRoundStart.Prefix
  ├─► [SnapshotSpread]   (Calibration) → ∆Calibration
  └─► [ResetFlags]       () → ∆RoundFlags

Entry:SetTile.Postfix
  └─► [CheckAndRecord]   (Entity, HostileFactions) → ∆LastSeen

Entry:ConsiderZones.Postfix
  ├─► [TrackScore]       (FactionIdx, TileScore) → ∆Calibration
  └─► [InjectGhost]      (FactionIdx, TileXZ, Ghosts, Calibration) → TileScore∆
```

## Strengths

- **Everything is visible**: inputs, outputs, dependencies — all declared in the graph. No hidden state reads/writes
- **Compile-time safety** (with F# DUs): the type system ensures nodes receive the right data
- **Testable**: each node is a pure function. Mock the inputs, assert the outputs
- **Composable**: add a node, wire it. Remove a node, the graph adapts (downstream nodes lose their input — detectable at registration time)
- **Cross-hook coordination is explicit**: a State node written by OnTurnStart and read by ConsiderZones is a visible edge in the graph
- **Analyzable**: the graph can be introspected — "what nodes run on OnTurnStart?", "what writes to Ghost state?", "what reads Calibration?"

## Weaknesses

- **Complexity**: the graph model is more complex than "register a callback." Mod authors need to understand nodes, edges, types, and topological execution
- **Type explosion**: each edge has a type. As the graph grows, the number of intermediate types grows too. Without careful design, the type vocabulary becomes hard to navigate
- **Rigid wiring**: adding a new node between two existing ones requires changing edges. In a pipeline, you just insert a step
- **Overhead**: topological sort, edge routing, type checking — runtime cost per hook invocation. For ConsiderZones (runs per tile, many times per turn), this matters
- **Not self-evident**: unlike a pipeline or event bus, the execution order isn't obvious from reading the code. You have to trace the graph

## Fit for BOAM

Most expressive, best long-term architecture. Captures the full structure of BooAPeek (and future mods) including cross-hook state coordination. But the complexity cost is real — this is a framework to build, not a pattern to apply. The question is whether the game's AI evaluation is complex enough to justify it.
