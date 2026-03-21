# Step 7: Config Externalization

**Date:** 2026-03-13
**Status:** COMPLETE

## Overview

Extracted all hardcoded rendering values from F# source into `config.json`, loaded once at startup. No hot reload — restart sidecar to apply changes.

## Config File

Location: `boam_tactical_engine/config.json` (copied to output directory by fsproj).

```json
{
  "port": 7660,
  "rendering": {
    "minTilePixels": 64,
    "gamma": 0.35,
    "fontFamily": "DejaVu Sans Mono",
    "scoreFontScale": 0.32,
    "labelFontScale": 0.33,
    "borders": {
      "actor":    { "margin": 2, "thickness": 3, "color": [255, 50, 50, 220] },
      "bestTile": { "margin": 1, "thickness": 2, "color": [50, 255, 50, 230] },
      "moveDest": { "margin": 3, "thickness": 2, "color": [60, 140, 255, 230] },
      "vision":   [255, 220, 50, 200]
    },
    "factionColors": {
      "0":  [128, 128, 128, 200],
      "1":  [60, 140, 255, 200],
      "2":  [80, 160, 255, 200],
      "3":  [200, 200, 100, 200],
      "4":  [100, 200, 100, 200],
      "5":  [200, 100, 50, 200],
      "6":  [180, 50, 180, 200],
      "7":  [255, 60, 60, 200],
      "8":  [160, 160, 160, 200],
      "9":  [200, 50, 50, 200]
    }
  }
}
```

## Values Externalized

| Config key | Was hardcoded in | Purpose |
|---|---|---|
| `port` | Program.fs | Sidecar listen port |
| `rendering.minTilePixels` | Rendering.fs | Upscaling threshold (tiles smaller than this get scaled up) |
| `rendering.gamma` | Rendering.fs | Background brightness (< 1 brightens; 0.35 aggressively lifts shadows) |
| `rendering.fontFamily` | HeatmapRenderer.fs | Font for score text and unit labels |
| `rendering.scoreFontScale` | HeatmapRenderer.fs | Score text size as fraction of tile pixels |
| `rendering.labelFontScale` | HeatmapRenderer.fs | Unit label size as fraction of tile pixels |
| `rendering.borders.actor` | Rendering.fs | Red border on analyzed actor's tile |
| `rendering.borders.bestTile` | Rendering.fs | Green border on highest-scoring tile |
| `rendering.borders.moveDest` | Rendering.fs | Blue border on actual move destination |
| `rendering.borders.vision` | HeatmapRenderer.fs | Yellow vision range border color |
| `rendering.factionColors` | FactionTheme.fs | Per-faction RGBA (keyed by faction index) |

## Implementation

### Config.fs
New module compiled first in the fsproj. Loads `config.json` from:
1. `AppContext.BaseDirectory` (next to the built DLL — production)
2. Three directories up from BaseDirectory (source dir — `dotnet run`)
3. Current working directory (fallback)

Exposes `Config.Current` as a singleton loaded at module init.

### Config types
```fsharp
type BorderConfig = { Margin: int; Thickness: int; Color: byte array }
type RenderingConfig = {
    MinTilePixels: int; Gamma: float32; FontFamily: string
    ScoreFontScale: float32; LabelFontScale: float32
    ActorBorder: BorderConfig; BestTileBorder: BorderConfig; MoveDestBorder: BorderConfig
    VisionColor: byte array; FactionColors: Map<int, byte array>
}
type SidecarConfig = { Port: int; Rendering: RenderingConfig }
```

### Consumers
Each module reads config once at module init:
- `FactionTheme.fs` — `cfg.FactionColors` → `Map.tryFind` with fallback to faction 0
- `Rendering.fs` — `cfg.MinTilePixels`, `cfg.Gamma`, border styles via `toBorder` helper
- `HeatmapRenderer.fs` — `cfg.FontFamily`, `cfg.ScoreFontScale`, `cfg.LabelFontScale`, `visionColor`
- `Program.fs` — `Config.Current.Port`

### fsproj
```xml
<None Include="config.json" CopyToOutputDirectory="PreserveNewest" />
```

## What's NOT in config

- **Faction icon names** (`factionIconName`) — string mappings, not visual tuning
- **Faction label prefixes** (`factionPrefix`) — same reasoning
- **Stopwords** — naming logic, not rendering config
- **Game paths** (gameDir, modFolder) — already configurable via `MENACE_GAME_DIR` env var

## Files Changed

| File | Change |
|------|--------|
| `config.json` | **New** — all rendering parameters |
| `Config.fs` | **New** — loader with singleton `Current` |
| `FactionTheme.fs` | Reads faction colors from config |
| `Rendering.fs` | Reads minTilePixels, gamma, border styles from config |
| `HeatmapRenderer.fs` | Reads font family, font scales, vision color from config |
| `Program.fs` | Reads port from config |
| `Sidecar.fsproj` | Added Config.fs + config.json copy rule |
