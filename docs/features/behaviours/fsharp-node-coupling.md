# F# Node Coupling — Current State

## What was done

All 5 behaviour nodes (Roaming, Reposition, Pack, GuardVip, Investigate) follow the self-contained pattern:
- Types, state keys, config types, defaults, and preset loading live in the node file
- Nodes self-register via `do Catalogue.register node` (module init triggered by banner `cfg` access in Program.fs)
- Nodes that receive C# events self-register their handler via `do EventHandlerRegistry.registerHandler`
- Config.fs BehaviourConfig reduced to `{ Hooks; Root }` + shared helpers (`readFloat`, `pickPreset`, `activePreset`)
- GameTypes.fs only holds shared types (TilePos, TileModifier, FactionState, etc.)
- Keys.fs only holds shared keys (tile-modifiers, actor-positions, etc.)
- Wire protocol renamed: `hook` → `event` throughout C# and F#
- `HookHandlers.fs` → `EventHandlers.fs`, `HookPayload.fs` → `EventPayload.fs`
- C# enrichment config key: `hooks` → `enrichments` in game_events.json5

## Files touched to add a new node

| File | Change |
|------|--------|
| `Nodes/YourBehaviour.fs` | Types, keys, config, event handler, node definition, self-registration |
| `behaviour.json5` | Execution chain entry + preset |
| `TacticalEngine.fsproj` | `<Compile>` entry |

If the node receives a C# event, the handler is self-registered via `EventHandlerRegistry.registerHandler` in the node file. No edit to EventHandlers.fs needed.

Program.fs must access the node module (e.g. `cfg` in the banner) to force module init. This is implicit — no explicit registration call needed.

## Remaining tensions

### 1. F# file ordering in fsproj

Every new file needs a `<Compile>` entry in the right position. This is a language constraint, not fixable.

### 2. Cross-node references

RepositionBehaviour reads `RoamingBehaviour.cfg.EngagementRadius`. This creates a compile-order dependency (Roaming must compile before Reposition). Moving shared config to a neutral location would break the self-contained pattern. Current state is acceptable — the dependency is explicit.

### 3. Program.fs banner must touch each node module

Nodes self-register via `do` blocks, but F# module init is lazy — something must access the module to trigger it. The banner in Program.fs accesses each node's `cfg`, which forces init. Adding a new node requires adding a banner line. This is implicit coupling but also useful (the banner should show the node's config anyway).

## Resolved tensions

- Types: node-specific types live in node files. GameTypes.fs is shared types only.
- Keys: node-specific keys live in node files (e.g. `investigateTargets` in InvestigateBehaviour). Keys.fs is shared keys only.
- Config: all preset types, defaults, and parsers live in node files. Config.fs has no node-specific code.
- Node registration: nodes self-register via `do Catalogue.register` in their module init. No explicit registration in Program.fs.
- Event dispatch: nodes self-register their C# event handlers via `EventHandlerRegistry.registerHandler` in a `do` block. EventHandlers.fs merges all node-registered handlers at startup — no per-node edits needed.
