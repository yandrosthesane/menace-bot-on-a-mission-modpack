# Candidate Architecture Evaluation

## Test Behaviors

Three concrete mods to evaluate each candidate against:

| Mod | Trigger | Reads | Writes | Cross-cutting |
|-----|---------|-------|--------|---------------|
| **BooAPeek** | OnTurnStart, ConsiderZones, OnRoundStart, SetTile | m_Opponents, LOS, tile scores | ghost state, UtilityScore | Cross-hook (ghosts created in OnTurnStart, read in ConsiderZones). Multi-threaded read in ConsiderZones. |
| **Potshot** | Behavior.Evaluate (InflictDamage) | ghost state (from BooAPeek), Skill ranges, map tiles | Behavior.Score for empty-tile targets | Cross-mod dependency (reads BooAPeek's ghosts). Must validate SkillTarget.EmptyTile flag. |
| **I'm Under Fire** | OnDamageReceived, OnTurnStart | damage event, retaliation ability, nearby allies | self RoleData (SafetyScale↑, evade), nearby allies' RoleData (UtilityScale↑ toward threat) | Cross-unit within same faction. State persists across hooks. |

## Game Architecture Constraints

These are non-negotiable — any candidate must work within them:

1. **ConsiderZones.Evaluate runs in parallel across tiles.** Shared state must be read-only during this phase.
2. **OnTurnStart fires per-unit, not per-faction.** 15 wildlife units = 15 calls. First call is the place to do per-faction setup.
3. **Agent evaluation is parallel across agents.** PostProcessTileScores, GetScoreMultForPickingThisAgent — no shared writes.
4. **Behavior.Evaluate runs per-behavior, per-agent.** Sequential within one agent, but agents are parallel.
5. **Hook points are fixed.** You get OnTurnStart, OnRoundStart, ConsiderZones.Evaluate, Entity.SetTile, Behavior.Evaluate, OnDamageReceived. You can't invent new game hooks.

---

## Event Bus

### How each mod registers

```
BOAM.on("OnTurnStart",     Prefix,  booapeek_filterOpponents)
BOAM.on("OnTurnStart",     Postfix, booapeek_maintainGhosts)
BOAM.on("ConsiderZones",   Postfix, booapeek_injectGhostBonus)
BOAM.on("OnRoundStart",    Prefix,  booapeek_resetAndSnapshot)

BOAM.on("InflictDamage",   Postfix, potshot_scoreEmptyTiles)

BOAM.on("OnDamageReceived",Postfix, underfire_markAndAlert)
BOAM.on("OnTurnStart",     Prefix,  underfire_applyRoleChanges)
```

### Strengths for modders

- **Lowest barrier to entry.** A modder writes one function, registers it on a hook. No framework concepts to learn beyond "hook name + timing."
- **BooAPeek fits naturally.** Four registrations, each a self-contained function.
- **I'm Under Fire is simple.** Register on OnDamageReceived, flag the unit, register on OnTurnStart to read flags and adjust roles.

### Tensions

- **Potshot depends on BooAPeek's ghost state but has no way to declare that.** The modder must know that `potshot_scoreEmptyTiles` needs `booapeek_maintainGhosts` to have run first. If BooAPeek isn't installed, Potshot silently gets empty ghost data — no error, no warning. The bus has no concept of "this handler requires data from that handler."
- **Ordering between mods is registration order only.** If a third mod registers an OnTurnStart handler between BooAPeek's filter and ghost maintenance, it sees a half-processed state. Nothing enforces "these two must be adjacent."
- **Cross-unit in I'm Under Fire is invisible.** The handler modifies *other units'* RoleData as a side effect. The bus doesn't know this happens. Two mods both adjusting nearby allies' roles will stomp each other — no conflict detection.
- **No structure to test against.** Each handler is a black box. The framework can't answer "what writes ghost state?" or "what reads RoleData?" — only the modder knows.

### Verdict

Good for solo mod development. Falls apart when mods interact or when a modder wants to understand what's happening across the system. The more mods registered, the harder it is to reason about execution.

---

## Pipeline / Middleware

### How each mod composes

```fsharp
BOAM.pipeline("OnTurnStart", Prefix)
  |> step "FilterOpponents"    (ctx → strip unseen, return ctx)
  |> step "RecordSightings"    (ctx → update LastSeen, return ctx)
  |> step "CreateGhosts"       (ctx → create ghosts for lost targets, return ctx)

BOAM.pipeline("ConsiderZones", Postfix)
  |> step "TrackScore"         (ctx → update calibration, return ctx)
  |> step "InjectGhostBonus"   (ctx → add ghost utility, return ctx)

BOAM.pipeline("InflictDamage", Postfix)
  |> step "ScoreEmptyTiles"    (ctx → score ghost positions as targets, return ctx)

BOAM.pipeline("OnDamageReceived", Postfix)
  |> step "CheckRetaliation"   (ctx → can this unit fight back?, return ctx)
  |> step "MarkFleeing"        (ctx → adjust self RoleData, return ctx)
  |> step "AlertNearby"        (ctx → adjust nearby allies, return ctx)
```

### Strengths for modders

- **Ordering is explicit and visible.** Step 1 runs before step 2, always. A modder can read the pipeline and understand the flow.
- **Context threading is natural in F#.** `ctx |> step1 |> step2 |> step3` — the output shape is clear at each stage.
- **I'm Under Fire decomposes cleanly.** CheckRetaliation → MarkFleeing → AlertNearby — each step has one job, testable in isolation.
- **BooAPeek's OnTurnStart is already a natural pipeline.** Filter → Record → Create is linear, no branching needed.
- **Easy to splice.** A new mod inserts a step between existing ones: `|> after "FilterOpponents" (step "LogFiltered" logFn)`.

### Tensions

- **Cross-pipeline state is still implicit.** The ghost state created in the OnTurnStart pipeline is consumed by the ConsiderZones pipeline. That link exists only because both pipelines close over the same mutable dictionary. The pipeline model doesn't capture this — it's a shared side effect, same as the event bus.
- **Potshot's cross-mod dependency is the same problem.** Potshot's InflictDamage pipeline reads ghost state written by BooAPeek's OnTurnStart pipeline. The pipeline model says nothing about this. If BooAPeek isn't installed, Potshot's step silently reads empty state.
- **Single context type per pipeline is constraining.** The OnTurnStart pipeline starts with opponents and ends with ghosts — the Context must carry both. As steps accumulate, Context becomes a grab-bag. The `ctx` in step 1 has fields that only step 5 uses.
- **No branching.** BooAPeek's "if previously seen → create ghost, else → record new sighting" must be an if/else inside a single step. The pipeline structure hides this decision point.
- **Splicing across mods is fragile.** If Potshot says "insert after BooAPeek.CreateGhosts", it couples to BooAPeek's step names. Rename the step, Potshot breaks.

### Verdict

Natural fit for within-hook logic. Clean F# ergonomics. But the interesting problems (cross-hook state, cross-mod dependencies, cross-unit effects) are all outside what the pipeline model expresses. You get a clean pipeline per hook and invisible wiring between them.

---

## Behavior Graph (SaneSRSEngine-style)

### How each mod defines its graph

```
Nodes:
  -- BooAPeek graph --
  CheckReady:
    entrypoint: OnTurnStart.Prefix
    run: ctx → if not tactical → stop
    next: FilterOpponents

  FilterOpponents:
    run: ctx → strip unseen, partition visible/removed
    next: ProcessResults

  ProcessResults:
    run: ctx → branch
    paths:
      - predicate: has LastSeen  → next: CreateGhost
      - default                  → next: RecordSighting

  CreateGhost:
    run: ctx → create ghost from LastSeen
    next: null

  RecordSighting:
    run: ctx → update LastSeen
    next: null

  -- Potshot graph --
  CheckGhosts:
    entrypoint: InflictDamage.Postfix
    run: ctx → read ghost state, find targetable positions
    next: ScoreEmptyTiles

  ScoreEmptyTiles:
    run: ctx → boost score for empty tiles near ghost waypoints
    next: null

  -- I'm Under Fire graph --
  OnHit:
    entrypoint: OnDamageReceived.Postfix
    run: ctx → check if can retaliate
    paths:
      - predicate: cannot retaliate → next: MarkAndAlert
      - default                     → next: null

  MarkAndAlert:
    run: ctx → flag self for fleeing, flag nearby allies for alert
    next: null

  ApplyFlags:
    entrypoint: OnTurnStart.Prefix
    run: ctx → read flags, adjust RoleData
    next: null
```

### Strengths for modders

- **Branching is first-class.** BooAPeek's "ghost or record?" fork is a declared `paths` array, not hidden in an if/else. Visible in the graph structure, visualizable with `graphToMermaid`.
- **Familiar if you've built this before.** The mental model is proven — you've shipped SaneSRSEngine with this pattern.
- **Each node is small and testable.** `FilterOpponents` does one thing. `CreateGhost` does one thing. Mock the context, assert the output.
- **Data-driven.** The graph is a data structure. You could serialize it, inspect it, diff two graphs, generate documentation from it.
- **I'm Under Fire's branching is clean.** "Can retaliate? → do nothing. Can't? → mark and alert." Two paths, explicit in the graph.

### Tensions

- **Cross-graph state is the same hidden problem.** Potshot's `CheckGhosts` node reads ghost state written by BooAPeek's `CreateGhost` node. That dependency is a shared mutable dictionary, not a graph edge. The graph captures within-entrypoint flow but not cross-entrypoint data flow.
- **Cross-mod dependency is still invisible.** Potshot's graph depends on BooAPeek's graph having run. The graph model has no concept of "this graph requires that graph's output." Same silent failure as event bus and pipeline if BooAPeek is missing.
- **String-keyed `next` pointers in F#.** `next: "CreateGhost"` is stringly-typed. Typo = runtime error. F# discriminated unions could fix this but then the graph is no longer data — it's types, and you lose the data-driven simplicity.
- **Sequential execution within a chain.** The graph runner walks node → next → next. No parallel paths within a single entrypoint. Not a real issue (game hooks are sequential checkpoints before parallel phases) but limits expressiveness.
- **Actions bag is untyped.** Each node receives `{ actions, context, args }`. In JS this is fine. In F# the lack of typed actions means either a big DU or obj casts — neither is pleasant.
- **I'm Under Fire's cross-unit effect is still a side effect.** `MarkAndAlert` modifies other units' state. The graph says nothing about the blast radius of a node — it looks like it only transforms its own context.

### Verdict

Best fit for within-hook logic that branches. The visualization and introspection story is strong. But it doesn't solve the actual hard problems any better than the pipeline — cross-hook state, cross-mod dependencies, and cross-unit effects are all outside the graph model. You get a nicer way to express branching logic, at the cost of translating a JS pattern into F# (which fights the language's type system).

---

## Typed Dataflow Graph

### How each mod declares its nodes

```
-- BooAPeek --
Node: FilterOpponents
  trigger: OnTurnStart.Prefix
  input:   AIFaction × Opponent list
  output:  Visible list × Removed list

Node: RecordSighting
  input:   from FilterOpponents.Visible
  output:  state∆ LastSeen

Node: CreateGhost
  input:   from FilterOpponents.Removed × state LastSeen
  output:  state∆ Ghost

Node: InjectGhostBonus
  trigger: ConsiderZones.Postfix
  input:   state Ghost × TileScore × Calibration
  output:  TileScore∆ (UtilityScore modified)

-- Potshot --
Node: ScoreGhostPositions
  trigger: InflictDamage.Postfix
  input:   state Ghost × Skill × Map
  output:  BehaviorScore∆

-- I'm Under Fire --
Node: DetectUnretaliatedFire
  trigger: OnDamageReceived.Postfix
  input:   Actor (self) × DamageEvent × Skill ranges
  output:  state∆ UnderFire

Node: FleeTowardFriendly
  trigger: OnTurnStart.Prefix
  input:   state UnderFire × Actor (self)
  output:  RoleData∆ (SafetyScale↑, evade=true)

Node: AlertNearbyAllies
  trigger: OnTurnStart.Prefix
  input:   state UnderFire × Actor list (same faction, nearby)
  output:  RoleData∆[] (UtilityScale↑ on nearby allies)
```

### Strengths for modders

- **Cross-hook state is explicit.** `state Ghost` is a named store. `CreateGhost` writes to it (OnTurnStart), `InjectGhostBonus` reads from it (ConsiderZones), `ScoreGhostPositions` reads from it (InflictDamage). All three edges are visible in the graph. This is the only candidate that captures this.
- **Cross-mod dependency is declared.** Potshot's `ScoreGhostPositions` declares `input: state Ghost`. If BooAPeek isn't installed, the framework knows at registration time that `state Ghost` has no writer. It can warn, error, or provide a default. No silent failure.
- **Cross-unit effects are visible.** `AlertNearbyAllies` declares `output: RoleData∆[]` — the bracket signals it affects multiple actors. Two mods both writing `RoleData∆` on nearby allies can be detected as a conflict at registration time.
- **Testable with typed contracts.** Each node is a pure function with typed inputs and outputs. Mock the inputs, assert the outputs. No context bag.
- **Analyzable.** "What writes Ghost state?" → query the graph. "What reads RoleData?" → query the graph. "What happens if I remove BooAPeek?" → the graph shows orphaned readers.
- **Thread safety is enforceable.** Nodes on `ConsiderZones.Postfix` can be validated at registration: "your inputs must be state reads, your outputs must be per-tile — no shared writes." The framework can reject unsafe registrations.

### Tensions

- **Complexity cost is real.** A modder writing BooAPeek needs to understand: nodes, typed inputs/outputs, state nodes, state deltas, trigger hooks, edge wiring. The event bus version is 4 function registrations. The dataflow version is 4 node declarations with typed edges, state node declarations, and wiring.
- **Type explosion.** Each edge has a type. `FilterOpponents.Visible` is `Opponent list`. `CreateGhost.input` is `Opponent × LastSeenPosition × Settings`. `InjectGhostBonus.input` is `Ghost × TileScore × Calibration`. As the graph grows, the intermediate type vocabulary grows. In F# this means a growing DU or record hierarchy.
- **Rigid wiring.** Inserting a new node between `FilterOpponents` and `CreateGhost` requires re-wiring edges. In a pipeline, you splice a step. In a dataflow graph, you change input/output declarations on both the new node and its neighbors.
- **Runtime overhead per hook.** Topological sort, edge routing, type validation — cost per invocation. ConsiderZones runs per-tile, potentially hundreds of times per turn. The framework overhead must be near-zero for hot paths.
- **Not self-evident.** Execution order comes from the graph topology, not from reading the code top-to-bottom. A modder must trace edges to understand what runs when. The pipeline's "step 1 → step 2 → step 3" is immediately obvious.
- **Framework to build, not pattern to apply.** This requires a runtime: node registry, edge resolver, topological sorter, state store, validation engine. The other three candidates are patterns you implement in an afternoon.

### Verdict

The only candidate that captures the actual hard problems: cross-hook state flow, cross-mod dependencies, cross-unit effects, and thread safety validation. But the cost is a real framework with real complexity. The question is whether the modding ecosystem will be large enough to justify that investment.

---

## Comparison Matrix

| Requirement | Event Bus | Pipeline | Behavior Graph | Typed Dataflow |
|-------------|-----------|----------|----------------|----------------|
| Modder writes a simple hook | ✅ trivial | ✅ one step | ⚠️ one node + wiring | ❌ node + types + edges |
| BooAPeek (within-hook) | ✅ | ✅ natural | ✅ branching is nice | ✅ |
| Potshot reads BooAPeek state | ❌ implicit | ❌ implicit | ❌ implicit | ✅ declared edge |
| I'm Under Fire cross-unit | ❌ invisible side effect | ❌ invisible side effect | ❌ invisible side effect | ✅ visible output |
| Missing dependency detected | ❌ silent | ❌ silent | ❌ silent | ✅ registration-time error |
| Ordering between mods | ❌ registration order | ⚠️ splice position | ⚠️ graph wiring | ✅ topological |
| Thread safety enforceable | ❌ modder's problem | ❌ modder's problem | ❌ modder's problem | ✅ framework validates |
| Visualizable / inspectable | ❌ handler list | ⚠️ step list per hook | ✅ graphToMermaid per hook | ✅ full cross-hook graph |
| F# ergonomics | ✅ functions | ✅ pipe operator | ⚠️ string keys or DU | ✅ typed records |
| Implementation cost | trivial | low | medium | high |

## The Core Tension

The three example mods expose a pattern:

- **BooAPeek** is self-contained. Any architecture handles it.
- **Potshot** depends on BooAPeek's output. Only typed dataflow makes this explicit.
- **I'm Under Fire** has cross-unit side effects. Only typed dataflow makes these visible.

The progression is: solo mod → cross-mod data dependency → cross-unit coordination. Each step up breaks one more candidate.

The event bus, pipeline, and behavior graph all solve the "within one hook, one mod" case well. They all fail identically on cross-hook state, cross-mod dependencies, and cross-unit effects — these become invisible shared mutable state.

Typed dataflow is the only candidate that captures these relationships. But it's also the only one that requires building a framework before any mod can be written.

## Possible Hybrid

The tension suggests a layered approach might work:

- **Layer 1: Pipeline per hook** — modders register steps on hooks, ordered, composable, natural F#. This is the 80% case. Simple mods need nothing else.
- **Layer 2: Declared state stores** — named, typed, framework-managed. Steps declare `reads: Ghost` and `writes: LastSeen`. The framework validates at registration: "Potshot reads Ghost but no installed mod writes Ghost → warning." This adds cross-hook and cross-mod visibility without full dataflow complexity.
- **Layer 3 (future): Coordination** — faction-level plans, spatial reasoning, cross-unit role assignment. Only built when needed.

This gives modders the pipeline's simplicity for the common case, with opt-in state declarations that catch the cross-mod/cross-hook problems. The framework doesn't need a topological sorter or edge router — just a state registry that tracks readers and writers.

Whether that hybrid is worth designing now vs. starting with a pure pipeline and adding state tracking later is the decision point.
