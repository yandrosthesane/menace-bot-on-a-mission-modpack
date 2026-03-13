# Candidate: Pipeline / Middleware

## Model

Ordered chain of transforms per hook point. Each step receives the output of the previous step and passes its result to the next. Like Express.js middleware or ASP.NET middleware pipeline.

```
BOAM.pipeline("OnTurnStart", Prefix)
  |> step "CheckReady"       (ctx → if not ready then ctx.Stop() else ctx)
  |> step "FilterOpponents"  (ctx → strip unseen from opponents, return ctx with filtered list)
  |> step "RecordSightings"  (ctx → for each visible: update LastSeen, return ctx)
  |> step "CreateGhosts"     (ctx → for each newly-invisible: create ghost, return ctx)

BOAM.pipeline("ConsiderZones", Postfix)
  |> step "TrackScore"       (ctx → update min/max calibration, return ctx)
  |> step "InjectGhost"      (ctx → add ghost bonus to tile score, return ctx)
```

Each step is a function `Context → Context`. Steps can short-circuit (stop the pipeline) or pass through.

## Execution model

```
Game hook fires
  → BOAM intercepts
  → Creates a Context from game data (faction, actor, tile, etc.)
  → Runs pipeline steps in declared order
  → Each step transforms the context and passes it forward
  → Final context is applied back to game data
  → Returns control to game
```

## BAP expressed as pipeline

```
OnTurnStart.Prefix pipeline:
  1. FilterOpponents  : ctx.Opponents → ctx.Opponents' (visible only)
  2. RecordSightings  : ctx.Opponents' → facts → state∆ LastSeen
  3. CreateGhosts     : ctx.Removed → state∆ Ghosts

OnTurnStart.Postfix pipeline:
  1. DecayGhosts      : ghosts → ghosts' (lower priority)
  2. ComputeWaypoints : ghosts' → ghosts'' (new waypoints)
  3. ExpireGhosts     : ghosts'' → ghosts''' (remove dead ones)

OnRoundStart.Prefix pipeline:
  1. SnapshotSpread   : calibration → calibration' (snapshot)
  2. ResetFlags       : round flags → cleared

SetTile.Postfix pipeline:
  1. CheckLOS         : entity → LOS results per hostile faction
  2. RecordSighting   : LOS results → state∆ LastSeen

ConsiderZones.Postfix pipeline:
  1. TrackScore       : tile score → calibration∆
  2. InjectGhost      : tile score → tile score' (+ ghost bonus)
```

## Strengths

- **Explicit ordering**: step 1 runs before step 2, always. No ambiguity
- **Data flows visibly**: each step's output is the next step's input
- **Composable**: a pipeline is a value. You can append, prepend, or splice steps
- **Testable**: each step is a pure function `Context → Context`, testable in isolation
- **Short-circuit**: a step can stop the pipeline early (e.g. "not in tactical, skip")

## Weaknesses

- **Linear only**: no branching. If step 3 needs to do different things based on step 2's output, that logic is inside step 3, invisible to the pipeline structure
- **One data shape**: all steps must work with the same Context type. If the pipeline crosses semantic boundaries (opponents → ghosts → tiles), the context becomes a grab-bag
- **Cross-pipeline coordination is awkward**: the ghost created in OnTurnStart needs to be read in ConsiderZones. That coordination happens through shared mutable state, outside the pipeline model
- **No event-driven reactions**: pipelines run synchronously per hook. There's no way for a step to say "when this happens later, do that" — it must happen within the current pipeline run

## Fit for BOAM

Good for the within-hook logic (BooAPeek's OnTurnStart prefix is already a natural pipeline). But doesn't capture the cross-hook coordination: OnTurnStart creates ghosts that ConsiderZones reads. That cross-hook link is implicit shared state, not part of the pipeline model.
