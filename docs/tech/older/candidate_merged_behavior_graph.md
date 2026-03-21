# Candidate: Merged Data-Driven Behavior Graph

## Model

Each mod declares nodes as data — a record with a name, hook binding, declared state reads/writes, a run function, and flow control (next, paths). The framework collects all nodes from all installed mods and merges them into a single graph. At registration time it validates dependencies, detects conflicts, and resolves execution order.

The graph is the single source of truth for "what happens when this hook fires."

## Node Declaration

```fsharp
type StateKey<'t> = StateKey of string

// Shared state keys — any mod can declare new ones
module BooAPeekState =
    let ghost      : StateKey<GhostMap>     = StateKey "BooAPeek.Ghost"
    let lastSeen   : StateKey<LastSeenMap>   = StateKey "BooAPeek.LastSeen"
    let calibration: StateKey<Calibration>   = StateKey "BooAPeek.Calibration"

module UnderFireState =
    let underFire  : StateKey<UnderFireSet>  = StateKey "UnderFire.Flagged"

// Node builder syntax
let booapeekNodes = [
    node "BooAPeek.FilterOpponents" {
        hook (OnTurnStart Prefix)
        reads Opponents
        writes Visible
        writes Removed
        next "BooAPeek.ProcessResults"
        run filterOpponents
    }

    node "BooAPeek.ProcessResults" {
        paths [
            when_ hasLastSeen "BooAPeek.CreateGhost"
            default_          "BooAPeek.RecordSighting"
        ]
    }

    node "BooAPeek.CreateGhost" {
        reads Removed
        reads BooAPeekState.lastSeen
        writes BooAPeekState.ghost
        run createGhost
    }

    node "BooAPeek.RecordSighting" {
        reads Visible
        writes BooAPeekState.lastSeen
        run recordSighting
    }

    node "BooAPeek.InjectGhostBonus" {
        hook (ConsiderZones Postfix)
        reads BooAPeekState.ghost
        reads BooAPeekState.calibration
        writes UtilityScore
        run injectGhostBonus
    }
]
```

A second mod adds its own nodes:

```fsharp
let potshotNodes = [
    node "Potshot.ScoreGhostTiles" {
        hook (InflictDamage Postfix)
        reads BooAPeekState.ghost    // cross-mod: references BooAPeek's state key
        reads SkillRange
        writes BehaviorScore
        run scoreGhostTiles
    }
]

let underFireNodes = [
    node "UnderFire.Detect" {
        hook (OnDamageReceived Postfix)
        reads DamageEvent
        reads SkillRanges
        writes UnderFireState.underFire
        run detectNoRetaliation
    }

    node "UnderFire.Flee" {
        hook (OnTurnStart Prefix)
        reads UnderFireState.underFire
        writes RoleData.Self
        run fleeTowardFriendly
    }

    node "UnderFire.AlertNearby" {
        hook (OnTurnStart Prefix)
        reads UnderFireState.underFire
        writes RoleData.Nearby
        run alertNearbyAllies
    }
]
```

## Registration & Merge

```fsharp
// Each mod registers its nodes in its plugin init
BOAM.register booapeekNodes
BOAM.register potshotNodes
BOAM.register underFireNodes
```

At registration time, the framework:

1. **Collects** all nodes into one pool
2. **Groups** by hook + timing (OnTurnStart.Prefix, ConsiderZones.Postfix, etc.)
3. **Resolves ordering** within each group:
   - Explicit `next` / `paths` chains define sequence
   - Nodes with no chain relationship are ordered by data dependency: if A writes X and B reads X, A runs before B
   - Nodes with no relationship to each other run in unspecified order (but deterministic)
4. **Validates**:
   - Every `reads` key has at least one `writes` key somewhere (cross-hook is fine)
   - No two nodes write the same state key on the same hook without explicit merge declaration
   - Every `next` target exists
   - No cycles within a hook group

## Execution

```
Game hook fires (e.g. OnTurnStart Prefix)
  → BOAM finds the merged subgraph for this hook + timing
  → Walks the graph:
       entry nodes (those with no incoming `next`) start
       each node:
         1. resolve reads from state store
         2. call run(ctx) → result
         3. apply writes to state store
         4. follow next / evaluate paths
       until no more nodes
  → Control returns to game
```

State store persists across hooks within a turn. Cleared per-turn or per-round as declared by each state key's lifetime.

## What the Merge Gives You

### Cross-mod dependency detection

```
Potshot installed without BooAPeek:
  → Potshot.ScoreGhostTiles reads BooAPeek.Ghost
  → No node writes BooAPeek.Ghost
  → Framework warns: "Potshot.ScoreGhostTiles reads 'BooAPeek.Ghost' but no installed mod writes it"
```

### Cross-hook state visibility

```
BooAPeek.CreateGhost writes Ghost     (OnTurnStart.Prefix)
BooAPeek.InjectGhostBonus reads Ghost (ConsiderZones.Postfix)
Potshot.ScoreGhostTiles reads Ghost   (InflictDamage.Postfix)

Framework knows Ghost flows: OnTurnStart → ConsiderZones, OnTurnStart → InflictDamage
```

### Cross-unit effect visibility

```
UnderFire.AlertNearby writes RoleData.Nearby

Framework knows this node has cross-unit side effects.
If another mod also writes RoleData.Nearby → conflict detected at registration.
```

### Visualization

The merged graph is data — generate a Mermaid diagram showing all nodes, edges, state flows, and hook boundaries across all installed mods.

```
graph LR
  subgraph "OnTurnStart.Prefix"
    FilterOpponents --> ProcessResults
    ProcessResults -->|hasLastSeen| CreateGhost
    ProcessResults -->|default| RecordSighting
    UnderFire.Flee
    UnderFire.AlertNearby
  end

  subgraph "ConsiderZones.Postfix"
    InjectGhostBonus
  end

  subgraph "InflictDamage.Postfix"
    Potshot.ScoreGhostTiles
  end

  CreateGhost -.->|Ghost| InjectGhostBonus
  CreateGhost -.->|Ghost| Potshot.ScoreGhostTiles
  UnderFire.Detect -.->|UnderFire| UnderFire.Flee
  UnderFire.Detect -.->|UnderFire| UnderFire.AlertNearby
```

## Merge Semantics

### 1. Independent entry points (different hooks)

No conflict. Each mod's nodes run on their own hooks. BooAPeek's OnTurnStart nodes and Potshot's InflictDamage nodes never interact at the execution level — only through state.

### 2. Same hook, no data dependency

Both run, order is unspecified but deterministic (alphabetical by node name, or registration order). UnderFire.Flee and UnderFire.AlertNearby both read UnderFire but don't depend on each other — either order is fine.

### 3. Same hook, data dependency

Automatic ordering. If ModA writes `Visible` on OnTurnStart.Prefix and ModB reads `Visible` on OnTurnStart.Prefix, ModA's node runs first. No explicit ordering needed.

### 4. Extending another mod's chain

A mod can insert into another mod's flow by declaring `after` or `before`:

```fsharp
node "Logging.AfterFilter" {
    after "BooAPeek.FilterOpponents"
    reads Visible
    run logVisibleOpponents
}
```

This splices into the chain: FilterOpponents → Logging.AfterFilter → ProcessResults.

**Alternative (less fragile):** target the data, not the node name:

```fsharp
node "Logging.AfterFilter" {
    hook (OnTurnStart Prefix)
    reads Visible                    // framework knows this needs to run after Visible is written
    run logVisibleOpponents
}
```

The framework orders it after FilterOpponents automatically because FilterOpponents writes Visible.

### 5. Extending a branch

A mod adds a new path to an existing branch node. The branch node must be declared `extensible`:

```fsharp
node "BooAPeek.ProcessResults" {
    extensible true
    paths [
        when_ hasLastSeen "BooAPeek.CreateGhost"
        default_          "BooAPeek.RecordSighting"
    ]
}

// Another mod extends it:
BOAM.extendPaths "BooAPeek.ProcessResults" [
    when_ isHighValueTarget "HVT.CreatePriorityGhost"   // inserted before default
]
```

The extended paths are evaluated in order: original paths first, then extensions, then default last.

## State Lifetime

Each state key declares its lifetime:

```fsharp
let ghost      = StateKey.perFaction "BooAPeek.Ghost"       // cleared when faction changes
let lastSeen   = StateKey.perSession "BooAPeek.LastSeen"    // persists entire tactical session
let underFire  = StateKey.perRound "UnderFire.Flagged"      // cleared each round
let calibration = StateKey.perRound "BooAPeek.Calibration"  // cleared each round
```

The framework manages lifecycle — modders don't manually reset state.

## Thread Safety

Nodes declare their threading context via their hook binding:

- `OnTurnStart.Prefix` — single-threaded, safe for shared writes
- `ConsiderZones.Postfix` — parallel across tiles, state reads only
- `InflictDamage.Postfix` — parallel across agents, state reads only

The framework validates at registration:

```
BooAPeek.InjectGhostBonus:
  hook = ConsiderZones.Postfix (parallel)
  reads Ghost ✓ (read-only in parallel context)
  writes UtilityScore ✓ (per-tile output, not shared state)

❌ Hypothetical bad node:
  hook = ConsiderZones.Postfix (parallel)
  writes Ghost ← REJECTED: shared state write in parallel context
```

## F# Ergonomics

### What a modder writes

```fsharp
module BooAPeek.Nodes

open BOAM

let ghost = StateKey.perFaction<GhostMap> "BooAPeek.Ghost"
let lastSeen = StateKey.perSession<LastSeenMap> "BooAPeek.LastSeen"

let filterOpponents (ctx: NodeContext) =
    let opponents = ctx.read Opponents
    let visible, removed = opponents |> List.partition (canSee ctx.Faction)
    ctx.write Visible visible
    ctx.write Removed removed

let nodes = [
    node "BooAPeek.FilterOpponents" {
        hook (OnTurnStart Prefix)
        reads Opponents
        writes Visible
        writes Removed
        next "BooAPeek.ProcessResults"
        run filterOpponents
    }
    // ... more nodes
]
```

### What the framework does

```fsharp
// In the mod's plugin init:
type BooAPeekPlugin() =
    interface IModpackPlugin with
        member _.Initialize(context) =
            BOAM.register BooAPeek.Nodes.nodes
```

The `run` function is just `NodeContext -> unit`. The `reads`/`writes` declarations are the contract — the framework trusts the function to respect them (same trust model as Harmony patches — you declare what you patch, the framework trusts your code).

## Strengths

- **Single mental model.** Everything is a node in a graph. No separate concepts for hooks, state, dependencies, ordering.
- **Cross-everything is visible.** Cross-hook state, cross-mod dependencies, cross-unit effects — all declared on nodes, all validated at merge time.
- **Data-driven.** The merged graph is inspectable, serializable, visualizable. Generate docs, Mermaid diagrams, dependency reports from the installed mod set.
- **Branching is first-class.** Paths with predicates, extensible by other mods.
- **Ordering is automatic.** Data dependencies drive execution order. No manual ordering, no registration-order fragility.
- **Thread safety is enforced.** The framework rejects unsafe writes in parallel contexts.
- **Graceful degradation.** Missing dependencies produce warnings, not crashes. Potshot without BooAPeek → warning at startup, ScoreGhostTiles gets empty ghost state.

## Weaknesses

- **Framework cost.** Requires building: node registry, merge engine, state store with lifetime management, validation engine, topological ordering, graph visualization. This is not an afternoon's work.
- **Declared reads/writes are trust-based.** A node that declares `reads Ghost` but also secretly reads `LastSeen` via closure won't be caught. The framework validates declarations, not code.
- **String-keyed node names for cross-mod references.** `after "BooAPeek.FilterOpponents"` couples to a name. Namespace conventions (mod prefix) help but don't eliminate the risk.
- **Learning curve.** A modder writing their first behavior must understand: nodes, state keys, reads/writes declarations, hook bindings, next/paths flow, lifetimes. The event bus equivalent is one function registration.
- **Overhead per hook invocation.** Graph traversal, state resolution, validation — adds cost. Must be near-zero for hot paths (ConsiderZones runs per tile).
- **extensible branches add complexity.** Multiple mods extending the same branch node creates ordering questions between extensions.

## Fit for BOAM

This candidate combines the behavior graph's branching and data-driven nature with the typed dataflow's cross-mod validation — without requiring full typed edges on every connection. The state key system is the bridge: lightweight enough to declare, powerful enough to validate.

The framework cost is the main risk. The question is whether to build the full framework up front or start with a minimal version (nodes + state keys + merge validation) and add branching/extension later.
