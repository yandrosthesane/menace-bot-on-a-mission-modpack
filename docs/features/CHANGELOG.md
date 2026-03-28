---
order: 10
---

# Changelog

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
