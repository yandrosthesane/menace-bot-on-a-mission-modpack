---
order: 10
---

# Changelog

## v2.5.0

### Engine infrastructure

- All paths resolved from game directory. Engine binary no longer needs to be adjacent to configs. Lookup chain: `MENACE_GAME_DIR` env var, then platform-specific Steam paths (Linux XDG + legacy, Windows Program Files).
- Engine binary moved from `Mods/BOAM/tactical_engine/` to `UserData/BOAM/Engine/`. Configs still in `Mods/BOAM/configs/` (mod defaults) and `UserData/BOAM/configs/` (user overrides).
- Engine version read from assembly metadata at runtime, stamped at build time with git hash (e.g. `2.5.0-dev.abc1234`).
- Windows support: path resolution uses `Program Files (x86)\Steam\...` when `MENACE_GAME_DIR` is not set.

### Icon setup

- Icon generator (`boam_asset_pipeline`) merged into the tactical engine. No separate `boam-icons` binary.
- Interactive setup on first startup when no icons found: generate from extracted game assets, fallback pack, or skip (colored squares used instead).

### Minimap

- Unit labels no longer show the actor index suffix (e.g. "Carda" instead of "Carda.1").

### Startup output

- Node registration output trimmed: one line per hook with node names, no per-node reads/writes detail, no state key validation warnings.

### Deploy

- `deploy.sh` publishes engine directly to `UserData/BOAM/Engine/`. Icon generation removed from deploy — handled by engine startup.

## v2.4.0

### Behaviour fixes

- Reposition idealRange now filters to `active.*` skills only. Vehicle ram skills (`special.*`) no longer drag ranged units to melee range.
- Units without active skills (workers) skip reposition instead of repositioning toward enemies.
- Reposition uses full AP budget when ideal attack range is unreachable. AP is only reserved for attacks when the unit can actually reach firing position this turn.
- Investigate formula reworked: tiles score by approach toward the target position relative to move distance. Closest reachable tile to the target gets full utility. Produces strong directional pull that scales with proximity.
- Investigate config values doubled (baseUtility 400, utilityFraction 1.6) to compete with pack behaviour.
- Pack influence halved (baseSafety 280, safetyFraction 0.6, initMultiplier 1.5) to reduce passive grouping.

### Diagnostic logging

- TileModifier application now logs before/after best tile with full score breakdown (U/S/D/A) and the modifier contribution on the chosen tile. Shows "SHIFTED" when the modifier changes the game's tile choice.

### Minimap

- Unit labels now show the actor index (e.g. "11" for wildlife, "Carda.1" for named units) for cross-referencing with engine logs.

### Self-contained node registration

- All behaviour nodes self-register via `do Catalogue.register` in their module init. No explicit registration in Program.fs.
- Nodes that receive C# events self-register their handler via `EventHandlerRegistry.registerHandler`. No edit to EventHandlers.fs needed for new nodes.
- All node config reads directly from `behaviour.json5` with no fallback defaults. Missing config fields crash at startup instead of silently using stale values.

### Naming cleanup

- Wire protocol renamed: `"type": "hook"` / `"hook": "..."` to `"type": "event"` / `"event": "..."`
- `HookHandlers.fs` renamed to `EventHandlers.fs`, `HookPayload.fs` to `EventPayload.fs`
- C# `QueryCommandClient.Hook()` renamed to `SendEvent()`
- Enrichment config key: `"hooks"` renamed to `"enrichments"` in game_events.json5
- Log tag `[HOOK]` renamed to `[EVNT]`
- Redundant `hook = "..."` fields removed from C# event payloads
- `game_events.json5` config version bumped to 7, `behaviour.json5` to 9

## v2.2.0

### Unified state

- All per-battle C# state consolidated into `Boundary/GameStore.cs` — a shared untyped store cleared on battle end.
- `TacticalMapState` data fields (map texture, tiles, heights, dirs, round, faction, active actor) backed by GameStore. Thread-safe unit access (lock + snapshot) stays local.
- `TileModifierStore` modifier data backed by GameStore. Synchronization (ManualResetEventSlim) stays local.
- `ActorRegistry` UUID mappings backed by GameStore.
- F# `currentRound` moved from mutable in HookHandlers to StateStore.

## v2.1.0

### Game events

- All C# data gathering is now gated by an `active` list in `game_events.json5` (separate config from behaviour.json5). 19 events, each independently enabled or disabled.
- Each event is self-contained in its own file under `src/GameEvents/` — owns its Harmony patches, data gathering, and sending.
- Manual patch registration moved from BoamBridge into each event's `Register()` method. BoamBridge.OnInitialize reduced to 5 Register calls.
- Inactive events produce no log output and skip all associated work.
- Eliminated `src/Hooks/` directory — AiObservationPatches, AiActionPatches, PlayerActionPatches, DiagnosticPatches, TileModifierPatch all absorbed into event files.
- See [Adding a Data Event](ADDING_A_GAME_EVENT) for the full guide.

### Features

- `game_events.json5` gains a `features` array: `["behaviour", "minimap"]` automatically activates all required events. The `active` list is additive on top.
- Available features: `behaviour`, `minimap`, `heatmaps`, `logging`

### Config restructure

- `modpack.json5` deleted (was empty)
- Rendering settings extracted from `engine.json5` into `heatmaps.json5`
- `engine.json5` reduced to network ports only
- F# feature flags (`heatmaps`, `action_logging`, `ai_logging`, `criterion_logging`) removed — derived from active game events
- Config-driven enrichment hooks: `game_events.json5` `hooks` section controls which events enrich which host events

### Icons

- Added pirate, vehicle, construct T2, rogue weapon team, and squad badge mappings to icon-config.json5 (86 icons total)

### Cleanup

- Removed unused `BOAM_PERSISTENT_ASSETS` environment variable (all paths derive from game directory)
- Removed unused `opponent_filter` config field and `ModpackConfig.cs`
- `SyncTransforms.cs` deleted — logic moved into ContactStateEvent and MovementBudgetEvent
- Lifecycle logic moved from BoamBridge into event Process() methods
- Diagnostic logging moved under the `action-logging` game event

## v2.0.4

### Tactical map reliability

- Map capture now writes to a fixed `battle_preview/` staging directory at mission prep, then copies to the timestamped battle report at tactical-ready. Previously the map background was sometimes missing when the tactical scene loaded.
- When the preview screen doesn't fire (e.g. continuing a save), the `LaunchMission` hook falls back to `Mission.GetPreview()` to capture the map data.
- `battle-start` hook now fires after the map copy, so the engine always receives the correct battle report directory.

### Score targeting

- Behaviour modifiers now target independent game score components instead of all going through UtilityScore. Each tile modifier carries per-score fields (utility, safety, distance, utilityByAttacks) applied independently by the C# patch.
- Roaming targets Utility, Reposition targets UtilityByAttacks, Pack targets Safety.
- Config property names updated to reflect their target score (e.g. `baseSafety`, `safetyFraction`, `maxUtilityByAttacks`).

### Reposition approach bias

- New `approachBias` parameter (0.0-1.0) breaks the symmetric ring around targets. Near-side tiles score higher than far-side tiles with equal ideal-range improvement, so melee units approach from the front instead of orbiting around.

### Guard VIP behaviour (new node, WIP — not active by default)

- `guard-vip-behaviour` draws same-faction units toward mission objective targets (ELIMINATE markers). Detects KillUnit objectives via `TacticalManager.Get().GetMission().Objectives` and adds Safety modifiers for tiles closer to objective actors. Add `"guard-vip-behaviour"` to the `OnTurnEnd` hook chain in `behaviour.json5` to enable.

## v2.0.3

Fix: modpack had a test data patch for Darby

## v2.0.2

CI release fix iteration.

## v2.0.1

CI release fix — release notes now correctly populate the GitHub Release description.

## v2.0.0

### AI behaviour system

Per-tile score modifiers injected during the game's tile evaluation. Three behaviour nodes run in a configurable chain:

- Roaming — explore outward when idle, suppressed near engagement
- Reposition — move toward closest known enemy at ideal attack range (scaled by 1/idealRange — melee gets strongest pull)
- Pack — form groups, converge on engaged allies, crowd penalty suppressed near combat

Pack init runs at battle start with a boost multiplier for aggressive initial formation.

See [Influencing AI Behaviour](README_BEHAVIOUR.md) for the full architecture and per-node docs.

### Configurable behaviour (`behaviour.json5`)

- Hook chains — define which nodes run on each game event, in what order. Remove a node to disable it.
- Named presets per behaviour — selectable via the `active` block
- Node catalogue — nodes register by name, config drives which ones are wired to which hooks
- Adaptive score scaling — modifiers use `max(default, gameMaxScore * fraction)` so they stay proportional to the game's own evaluation

See [Adding Nodes](behaviours/ADDING_A_BEHAVIOR_NODE) for a guide to creating new behaviours.

### Symmetric protocol

C# bridge and F# engine communicate via two endpoints:

- `POST /query` — read-only (status, features)
- `POST /command` — side effects (hooks, tile modifiers)

Both directions use `{"type": "..."}` dispatch. Replaces the previous 15+ individual HTTP routes.

### Architecture

- Thread-safe StateStore (ConcurrentDictionary)
- OnTacticalReady hook point — nodes run their own init at battle start via the walker
- Faction-aware pack scoring — player units no longer treated as wildlife allies
- Static data pipeline — skills and movement costs stored once at tactical-ready
- C# sync transforms — contact detection (`IsDetectedByFaction` + vision range) and movement budget computed from live game objects
- Batch tile modifier flush — single POST replaces N sequential round-trips
- Walker logs `>>` / `<<` markers per node for easy log parsing

### Minimap

- No-label display preset (`FontSize: 0` now correctly hides labels)
- All presets now show all fields for easier customization

### Fixes

- Engine connection race — multiple check threads no longer overwrite each other
- Template AP fallback — reads from `EntityTemplate.Properties.ActionPoints` when `GetActionPointsAtTurnStart()` returns 0
- `boam-launch.sh` kills stale engine processes before starting
- `idealRange` included in dramatis personae
- Contact detection uses game's `IsDetectedByFaction` instead of raw distance check

### CI

- Release workflow — push a version tag to build all archives and create a GitHub Release with changelog notes
- Documentation workflow builds and deploys to GitHub Pages

### Removed

- `BehaviorOverridePatch.cs`, `ShapeTileModifier.fs`, `EngineClient.cs`, `CommandServer.cs`
- Inline test node from Program.fs
- 15+ individual hook routes

---

## v1.3.0

### Tile modifier system
Engine-controlled tile score injection for directing AI unit movement. Supports target-tile mode (gradient toward a position), distance-gating mode (flat bonus in range), attack suppression, and forced idle on arrival. See [Tile Modifier System](../tech/up_to_2.0/1_tile-modifier-system.md).

### Modpack config (`modpack.json5`)
Independent config file for the C# bridge, separate from the engine's `engine.json5`. Loaded with the same two-tier resolution (user → default) and config version check. Currently gates `opponent_filter`.

### Config rename
`config.json5` renamed to `engine.json5` for clarity. The engine config seeds and resolves identically.

### Map capture fix
Map background and tile data now captured at `LaunchMission` (right before scene transition) instead of only `OnPreviewReady`. Fixes missing map data when the preview was already cached. Toast notification on capture.

### Zero-config icon generation
Icons are now generated automatically from extracted game assets. No manual copying of PNGs into `UserData/BOAM/` is needed — the icon generator reads directly from `UserData/ExtractedData/Assets/`. The tactical engine auto-generates icons on startup if none are found. Generated icons are stored in `UserData/BOAM/icons/` and survive mod deploys.

### Steam launch integration
New `boam-launch.sh` / `boam-launch.bat` scripts start the tactical engine via Steam Launch Options. The engine runs in a separate window alongside the game and stays running after the game exits.

### Cross-platform release improvements
- Replaced 7z with zip for release archives
- Removed old `generate-icons.sh` (python/ffmpeg) — replaced by `boam-icons` binary
- `boam-icons` / `boam-icons.exe` included in all release variants
- All config paths are now relative and portable (Linux and Windows)

### Engine startup health check
The tactical engine banner now displays resolved paths (game dir, mod dir, data dir, reports dir) and icon count with color-coded status.

### Icon directory moved to UserData
Icons are now read from `UserData/BOAM/icons/` instead of `Mods/BOAM/icons/`. This applies to both the C# minimap overlay and the F# tactical engine. Icons survive mod deploys without regeneration.

### Engine no longer shuts down on game exit
The C# bridge no longer sends `/shutdown` to the tactical engine when the game closes. The engine stays running independently — useful for rendering heatmaps after a session.

### Passive engine start by default
The Steam launch scripts start the engine in passive mode (no `--on-title` auto-navigation). Use `start-tactical-engine.sh --on-title /navigate/tactical` for dev workflows.

---

## v1.2.0

### Standalone minimap
The minimap now works without the BOAM-engine running. Only start the engine when you need heatmaps or action logging.

### Bounded context architecture
Both the BOAM-modpack and BOAM-engine are reorganized into bounded contexts with independent types and clear separation of concerns.

**BOAM-modpack:**
- `Minimap/` — self-contained overlay (types, state, renderer, map loader)
- `Hooks/` — Harmony patches (AI observation, player actions, diagnostics)
- `Engine/` — BOAM-engine communication (HTTP client, command server/executor)
- `Boundary/` — config loading and resolution
- `Tactical/` — game domain (actor UUID registry)
- `Utils/` — shared utilities (toast, JSON5 parser, color parser, naming helpers)

**BOAM-engine:**
- `Domain/` — shared game primitives (TilePos, FactionState)
- `Boundary/` — payload DTOs, config, logging, hook parsing, action logging, event bus
- `NodeSystem/` — behavior graph framework (state keys, nodes, walker)
- `Heatmaps/` — rendering pipeline with own types, fully decoupled from game domain

### Config auto-seeding
On first run, each component automatically copies its config to `UserData/BOAM/configs/` from mod defaults. No manual copying needed.

### Engine startup banner
The BOAM-engine now shows config source and feature status at startup:
```
Config:  user (v2)  .../UserData/BOAM/configs/engine.json5
─────────────────────────────────
●  Minimap
○  Heatmaps
○  Action logging
○  AI decision logging
```

### Replay system removed
The experimental replay system (decision forcing, combat outcome forcing, determinism watchdog) has been removed. Action logging and AI decision capture remain for analysis.

---

## v1.1.0

- **Feature gates** — Heatmaps, action logging, AI logging are opt-in. Only minimap enabled by default.
- **Production launcher** — Engine opens a dedicated terminal with live output and log file.

## v1.0.0

Initial release: BOAM-modpack, BOAM-engine, minimap, heatmap renderer, icon generator.
