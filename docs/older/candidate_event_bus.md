# Candidate: Event Bus

## Model

Flat publish/subscribe. Handlers register for game hook events by name. When a hook fires, all registered handlers for that event run in registration order.

```
BOAM.on("OnTurnStart",  Prefix,  filterOpponents)
BOAM.on("OnTurnStart",  Postfix, updateGhostWaypoints)
BOAM.on("OnRoundStart", Prefix,  resetRoundFlags)
BOAM.on("SetTile",      Postfix, recordMidMoveSighting)
BOAM.on("ConsiderZones",Postfix, injectGhostBonus)
```

Each handler is a function that receives a context bag and can:
- Mutate game data (filter opponents, inject scores)
- Mutate shared state (record sightings, update ghosts)
- Return nothing (fire-and-forget)

## Execution model

```
Game hook fires
  → BOAM intercepts (owns the Harmony patch)
  → Collects all registered handlers for this hook + timing (prefix/postfix)
  → Runs them sequentially in registration order
  → Returns control to game
```

## BAP expressed as event bus

```
register("OnTurnStart",  Prefix,  ctx → filter ctx.Opponents by LOS, record sightings)
register("OnTurnStart",  Postfix, ctx → decay ghosts, compute waypoints, expire old)
register("OnRoundStart", Prefix,  ctx → snapshot spread, reset flags)
register("SetTile",      Postfix, ctx → check LOS from hostile factions, record if seen)
register("ConsiderZones",Postfix, ctx → add calibrated ghost bonus to tile score)
```

## Strengths

- **Simple**: one concept (handler on event), flat structure, no graph to reason about
- **Familiar**: same pattern as DOM events, C# events, Harmony itself
- **Low barrier**: a mod author writes one function per behavior, registers it
- **Easy to debug**: list all handlers, see execution order

## Weaknesses

- **No explicit data flow**: handlers share state via side-channel (shared dict, mutable singleton). Who writes what, who reads what — invisible at the registration level
- **Ordering is fragile**: handler A must run before handler B, but that's encoded only by registration order. Nothing enforces it. If a third mod registers between them, things break
- **No composability**: two handlers can't be combined into a pipeline. Each is an isolated callback
- **State management is ad-hoc**: each mod manages its own state. No structure to say "this handler produces facts that this other handler consumes"
- **No branching**: every handler runs unconditionally. Conditional logic lives inside each handler, invisible to the framework

## Fit for BOAM

Adequate for simple cases but doesn't generalize well. BooAPeek's logic is already an implicit pipeline (filter → record → inject), and an event bus hides that structure. As more mods register on the same hooks, the flat handler list becomes hard to reason about.
