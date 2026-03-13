# Candidate: Behavior Graph (SaneSRSEngine-style)

## Inspiration

Your SaneSRSEngine userscript uses a declarative behavior graph:
- **Nodes** are named steps with a `run` function, static `args`, and a `next` pointer
- **Entrypoints** are nodes triggered by external events (key press, XHR, custom events)
- **Execution** walks from entrypoint → next → next until `next: null` or `end: true`
- **Context** flows through: each node receives `{ actions, context, args }` and returns `{ next, context }`
- **Branching** via `args.paths` — an array of `{ predicate, next }` evaluated in order
- **Retry** via `retry: { attempts, delay, predicate }` — poll until condition clears
- **Actions** are injected capabilities (matchers, renderers, state ops) resolved from a module registry
- **Boundaries** bind DOM/custom events to graph entrypoints

The graph is data (a JS object), not code. The runner is generic. New behaviors are added by defining new nodes and wiring `next` pointers.

## Translated to the Menace AI domain

```
Nodes:
  CheckReady:
    entrypoint: OnTurnStart.Prefix
    run: ctx → if not in tactical or factions not discovered → stop
    next: FilterOpponents

  FilterOpponents:
    run: ctx → strip unseen from m_Opponents via LOS checks
    next: ProcessRemovals

  ProcessRemovals:
    run: ctx → for each removed opponent, branch:
    paths:
      - predicate: had LastSeen entry  → next: CreateGhost
      - default                        → next: RecordNewSighting

  RecordNewSighting:
    run: ctx → update LastSeen for visible actors
    next: null (end)

  CreateGhost:
    run: ctx → create GhostMemory from LastSeen, pre-multiply priority
    next: null (end)

  -- Separate graph triggered on OnTurnStart.Postfix --
  GhostMaintenance:
    entrypoint: OnTurnStart.Postfix
    run: ctx → decay priorities, compute waypoints
    next: ExpireCheck

  ExpireCheck:
    run: ctx → remove ghosts past TTL
    next: null (end)

  -- Separate graph triggered on ConsiderZones.Postfix --
  ScoreInjection:
    entrypoint: ConsiderZones.Postfix
    run: ctx → track min/max, inject calibrated ghost bonus
    next: null (end)
```

## Execution model

```
Game hook fires (e.g. OnTurnStart)
  → BOAM finds all entrypoint nodes for this hook + timing
  → For each entrypoint, runs the graph:
       node.run({ actions, context, args }) → { next, context }
       follow `next` pointer to next node
       repeat until next is null or end is true
  → Context mutations applied to game state
```

## Key adaptation from SaneSRSEngine

| SaneSRSEngine | Menace AI domain |
|---------------|-----------------|
| DOM events (keydown, click) | Harmony hook points (OnTurnStart, SetTile) |
| Event boundaries (predicate on event type) | Hook trigger + prefix/postfix timing |
| `actions` (matchers, renderers, state ops) | SDK functions (LOS checks, faction queries, score reads) |
| `context` (currentCard, userState) | AI context (faction, actors, opponents, tile scores) |
| `args.paths` with predicates | Branching based on LOS results, ghost state |
| `retry` with delay | Not directly needed (game runs synchronously per hook) |
| Module registry + factory pattern | F# module functions + DI at registration |

## Strengths

- **Proven**: you've built and shipped this pattern. The mental model is familiar
- **Data-driven**: the graph is a data structure, not scattered code. Inspectable, serializable, visualizable (you already have `graphToMermaid`)
- **Branching is first-class**: `paths` with predicates handle the "filter → record or create ghost" fork naturally
- **Context threading**: each node transforms context and passes it forward. State flows explicitly through the chain
- **Entrypoint multiplexing**: multiple entrypoints on different hooks, each running its own subgraph. Mirrors how different hooks trigger different BooAPeek logic
- **Extensible**: a new mod adds new nodes and wires them into existing chains via `next` pointers

## Weaknesses

- **Sequential only within a chain**: the graph is a linked list with branches, not a DAG. Two independent nodes can't run in parallel (not a real issue in the synchronous game context, but limits expressiveness)
- **Cross-graph state is still implicit**: the "OnTurnStart creates ghosts" → "ConsiderZones reads ghosts" link is through shared mutable state, not a graph edge. The graph model captures within-hook flow but not cross-hook dependencies
- **String-keyed `next` pointers**: `next: "CreateGhost"` is stringly-typed. Typo = runtime error. F# DUs could replace this with compile-time checked transitions, but that changes the flavor from data-driven to type-driven
- **Actions bag is a grab-bag**: `actions` contains everything any node might need. No type safety on what a node actually uses. F# would want narrower interfaces
- **Not idiomatic F#**: the pattern is fundamentally imperative (mutable context, while-loop runner, string dispatch). Translating it to F# would either fight the language or lose the data-driven simplicity

## Fit for BOAM

Strong fit for the mental model — you know how to think in this pattern. The translation challenge is making it feel natural in F# rather than a JS port. The cross-hook state coordination gap is the same as the pipeline candidate: solvable, but outside the graph model proper.

The main question: do you want the graph to be data (JSON/object, interpreted at runtime, like SaneSRSEngine) or types (F# DUs, checked at compile time, less flexible but safer)?
