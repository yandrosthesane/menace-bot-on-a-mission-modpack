# F# Node Coupling — Current State

## What was done

All 5 behaviour nodes (Roaming, Reposition, Pack, GuardVip, Investigate) follow the self-contained pattern:
- Types, state keys, config types, defaults, and preset loading live in the node file
- Config.fs BehaviourConfig reduced to `{ Hooks; Root }` + shared helpers (`readFloat`, `pickPreset`, `activePreset`)
- GameTypes.fs only holds shared types (TilePos, TileModifier, FactionState, etc.)
- Keys.fs only holds shared keys (tile-modifiers, actor-positions, etc.)

## Files touched to add a new node

| File | Change |
|------|--------|
| `Nodes/YourBehaviour.fs` | Types, keys, config, hook handler, node definition |
| `behaviour.json5` | Hook chain entry + preset |
| `TacticalEngine.fsproj` | `<Compile>` entry |
| `Program.fs` | `Catalogue.register` (one line) |

If the node receives a C# event, the hook handler is self-registered via `EventHandlerRegistry.registerHandler` in the node file. No edit to HookHandlers.fs needed.

## Remaining tensions

### 1. Nodes are manually registered in Program.fs

Every node needs `Catalogue.register` in Program.fs. F# module `do` blocks don't work reliably — module init is lazy and ordering isn't guaranteed before config parsing.

This is one line per node. Low friction but still a central edit. The `Catalogue.register` call also forces module init, which triggers any `do` blocks (hook registry, etc.).

### 3. F# file ordering in fsproj

Every new file needs a `<Compile>` entry in the right position. This is a language constraint, not fixable.

### 4. Cross-node references

RepositionBehaviour reads `RoamingBehaviour.cfg.EngagementRadius`. This creates a compile-order dependency (Roaming must compile before Reposition). Moving shared config to a neutral location would break the self-contained pattern. Current state is acceptable — the dependency is explicit.

## Resolved tensions

- Types: node-specific types live in node files. GameTypes.fs is shared types only.
- Keys: node-specific keys live in node files (e.g. `investigateTargets` in InvestigateBehaviour). Keys.fs is shared keys only.
- Config: all preset types, defaults, and parsers live in node files. Config.fs has no node-specific code.
- Hook dispatch: nodes self-register their C# event handlers via `EventHandlerRegistry.registerHandler` in a `do` block. HookHandlers.fs merges all node-registered hooks at startup — no per-node edits needed.
