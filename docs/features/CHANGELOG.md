# Changelog

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
Config:  user (v2)  .../UserData/BOAM/configs/config.json5
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
