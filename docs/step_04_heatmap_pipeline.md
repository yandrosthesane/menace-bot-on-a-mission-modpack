# Step 4: Heatmap Pipeline — AI Tile Score Visualization

**Date:** 2026-03-13
**Status:** WORKING

## Overview

Full pipeline for visualizing AI tile scores as heatmap images overlaid on the tactical map background. Scores are rendered as compact numerical values per tile, showing the AI's weighted evaluation of each position. Unit overlays show all actors with game badge icons and leader labels.

## Architecture

```
Game (Wine/Proton)                      Linux Native
┌─────────────────────┐                ┌───────────────────────────┐
│ src/BoamBridge.cs    │   HTTP/JSON   │ boam_tactical_engine/     │
│                      │ ────────────> │                           │
│ Harmony Patches:     │  port 7660    │ HeatmapRenderer.fs        │
│  OnTurnStart         │               │  + ImageSharp             │
│  PostProcessTiles    │               │  + mapbg.png overlay      │
│                      │               │  + badge icons            │
│ Unit data:           │               │                           │
│  EntitySpawner       │               │ Program.fs (HTTP server)  │
│  UnitActor.GetLeader │               │ GameTypes.fs (contracts)  │
└──────────────────────┘               └──────┬────────────────────┘
                                              │
                                              ▼
                                    Mods/BOAM/heatmaps/
                                    combined_W_stinger_13.png

boam_asset_pipeline/
  icon-config.json ──> generate-icons.sh ──> Mods/BOAM/icons/
                        (ffmpeg resize)        factions/*.png
                                               templates/*.png
```

## Hook Points

### 1. `AIFaction.OnTurnStart` (Prefix)
- **When:** Once per faction, before any agent evaluation
- **Threading:** Sequential — safe for HTTP
- **Data sent:** faction index, opponent list (actorId, position, TTL, isKnown, isAlive)
- **Endpoint:** `POST /hook/on-turn-start`

### 2. `Agent.PostProcessTileScores` (Postfix)
- **When:** After ALL criterions have evaluated tiles AND role-based weighting is applied
- **Threading:** Parallel across agents, sequential per agent — safe for HTTP (each agent independent)
- **Data sent:** faction, actorId, actorName, actorPosition, tiles (x, z, combined), units (all alive actors with leader names)
- **Endpoint:** `POST /hook/tile-scores`

### 3. `TacticalManager.InvokeOnMovementFinished` (via SDK event)
- **When:** After an actor completes movement and stops (AP exhausted or destination reached)
- **Threading:** Sequential — fires on the main thread
- **Data sent:** faction, actorId, tile (x, z) — the actual tile where the unit stopped
- **Endpoint:** `POST /hook/movement-finished`
- **Usage:** The sidecar stores the last move destination per actor. When rendering a heatmap, if a move destination exists for that actor, a **blue border** is drawn on the tile the unit actually reached — contrasting with the **green border** on the best-scored tile (intended target).

**Why this hook and not `ConsiderZones.PostProcess`:**
The evaluation flow inside `Agent.Evaluate()` is:
```
EvaluateTilesWithCriterions()    — Criterion.Collect phase
CollectBehaviors()
EvaluateTilesSecondPass()        — ALL criterions' Evaluate() on all tiles (parallel)
PostProcessTileScores()          — FinalScore = U×Uscale + S×Sscale - D×Dscale  ← WE HOOK HERE
  foreach criterion.PostProcess()  — minor adjustments
EvaluateBehaviors()
PickBehavior()
```

## Unit Data Pipeline

The bridge gathers unit info for the heatmap overlay:

```csharp
// For each alive actor via EntitySpawner.ListEntities(-1):
{
    faction: aInfo.FactionIndex,
    x: position.x, z: position.y,
    name: m_Template.GetName(),     // e.g. "enemy.alien_stinger"
    leader: UnitActor.GetLeader()   // e.g. "Rewa" (player units only)
             .GetNickname()
             .GetTranslated()
}
```

**Leader chain:** `UnitActor` → `GetLeader()` → `BaseUnitLeader` (Il2CppMenace.Strategy) → `GetNickname()` → `LocalizedLine` (Il2CppMenace.Tools) → `GetTranslated()` → string

Wildlife/civilian actors don't have leaders — the try/catch silently falls back to empty string.

## Heatmap Rendering

**Input:** TacticalMap mod's `mapbg.png` + `mapbg.info` (texW, texH, tilesX, tilesZ)
**Output:** `Mods/BOAM/heatmaps/combined_{faction}_{template}_{actorId}.png`

Features:
- **Background** — gamma-corrected (`pow(channel/255, 0.35) * 255`) for readability
- **Tile scale** — upscaled to 64px minimum per tile
- **Score text** — compact format: `4.3k`, `-971`, `0.0` (k suffix for >=1000)
- **Actor marker** — red 3px border on the current actor's tile
- **Best tile marker** — green 2px border on the highest combined score tile (intended target, may be beyond movement range)
- **Actual destination marker** — blue 2px border on the tile the unit actually reached after moving (AP-limited)
- **Unit icons** — game badge art, resolved via fallback chain (see below)
- **Labels** — `{FactionPrefix}_{displayName}_{index}` (e.g., `P_rewa_1`, `W_stinger_11`)
- **Label visibility** — only enemies (different faction) + the analyzed actor itself

**Libraries:** SixLabors.ImageSharp 3.1.12, ImageSharp.Drawing 2.1.7, Fonts 2.1.3

## Icon System

### Fallback chain

Resolution order for each unit's icon:

1. **Leader icon** — `icons/templates/{leader_name}.png` (e.g., `rewa.png`)
2. **Template icon** — `icons/templates/{template_name}.png` (e.g., `alien_stinger.png`)
3. **Faction icon** — `icons/factions/{faction}.png` (e.g., `wildlife.png`)
4. **Colored square** — hard fallback, filled with faction color

When a higher-priority icon is missing, the faction icon is auto-copied to the expected path so the user has the correct filename to replace with a real icon.

### Asset pipeline

Icons are generated from game badge art via `boam_asset_pipeline/`:

- `icon-config.json` — declares named source directories and source→output mappings
- `generate-icons.sh` — reads config, resizes PNGs via ffmpeg (Lanczos)
- Default size: 64x64, per-entry `"size"` override supported

**Source directories** are named (`native`, `placeholders`, `custom`) so entries are clean:
```json
{ "dir": "native", "source": "badges/leaders/rewa_badge_234x234.png", "output": "templates/rewa.png" }
```

**Important:** Deploy wipes `Mods/BOAM/`. Always run `generate-icons.sh` after deploy.

### Label construction

- **Short name** — template name after last `.`, stopword-filtered (`alien`, `soldier`, `01`, `small`, `big`, `light`, `heavy`, `comp` removed), last 2 segments kept
- **Display name** — leader nickname preferred over template short name
- **Label format** — `{FactionPrefix}_{displayName}_{factionIndex}` (e.g., `P_rewa_1`, `W_stinger_11`)

### Stopwords

Noise words stripped from template names for compact labels:
`alien`, `soldier`, `civilian`, `enemy`, `small`, `big`, `light`, `heavy`, `comp`, `01`-`05`

## Icon rendering

- Icons are **not tinted** — badge art has baked-in faction colors
- Resized to tile size via Lanczos3 resampling
- Composited with alpha blending (transparent badge corners show map background)
- Icon cache uses `ConcurrentDictionary` for thread safety (multiple agents fire in parallel)

## Score Interpretation

The combined score is: `Utility × UtilityScale + Safety × SafetyScale - Distance × DistanceScale`

- **High positive (3k-5k):** Strongly preferred tiles — zone objectives, good cover, close
- **Low positive (0-1k):** Acceptable tiles, no strong pull
- **Negative (-1k to -4k):** Distance penalty outweighs benefits — reachable but unattractive
- **Zero (0.0):** Current tile or tiles with no criterion contributions
- **Best tile** (green marker) is what the AI's Move behavior will target

## Confirmed Il2Cpp Agent API

Discovered via REPL `typeof(Il2CppMenace.Tactical.AI.Agent).GetMethods()`:

| Method/Property | Purpose |
|----------------|---------|
| `PostProcessTileScores()` | Hookable, no params, applies role weights to m_Tiles |
| `get_m_Actor` / `m_Actor` | Actor reference |
| `get_m_Tiles` / `m_Tiles` | Dictionary<Tile, TileScore> |
| `GetRole()` | Returns RoleData (criterion scales, behavior weights) |
| `GetBehaviors()` | Returns behavior list |
| `GetScore()` | Agent's selected behavior score |
| `PickBehavior()` | Selects best behavior |
| `EvaluateBehaviors()` | Scores all behaviors against tiles |

TileScore:
| Method/Property | Purpose |
|----------------|---------|
| `GetScore()` | Weighted combined score |
| `UtilityScore` | Accumulated from ConsiderZones, ExistingTileEffects, etc. |
| `SafetyScore` | Accumulated from CoverAgainstOpponents, ThreatFromOpponents, etc. |
| `DistanceScore` | Accumulated from DistanceToCurrentTile |

## Bug Fixes During Development

### Sidecar not detected (scene check)
`OnSceneLoaded` only checked `sceneName == "Title"` for sidecar discovery. But loading a save goes Splash→Strategy→Tactical, skipping Title entirely.
**Fix:** Check sidecar on every scene load (removed Title-only guard).

### Zero scores everywhere (wrong hook)
`ConsiderZones.PostProcess` only captures zone bonus contributions to UtilityScore. Other criterions (Cover, Threat, Distance) had already scored tiles in `EvaluateTilesSecondPass`, but ConsiderZones only adds its own zone bonuses in PostProcess.
**Fix:** Switched to `Agent.PostProcessTileScores` which runs after all criterions and role weighting.

### Unreadable numbers
Raw float values like `3876.9` overflow tile boundaries.
**Fix:** Compact format with `k` suffix — `3.9k` for thousands, `971` for hundreds, `0.0` for small values.

### Dark background (linear brightness)
ImageSharp `Brightness()` processor had no visible effect on near-black source pixels.
**Fix:** Per-pixel gamma correction: `pow(channel/255, 0.35) * 255`.

### File locking on concurrent writes
Multiple agents with same template name wrote to same PNG filename.
**Fix:** Added `actorId` to filename for uniqueness.

### ConcurrentDictionary for icon cache
Plain `Dictionary` caused "concurrent collection corrupted" when multiple tile-scores requests hit in parallel.
**Fix:** `ConcurrentDictionary.GetOrAdd`.

### Tinted badges too dark
`tintIcon` multiplied RGB by faction color, darkening pre-colored badge art.
**Fix:** Removed tinting — badges have baked-in faction colors. Just resize with Lanczos3.

## Files

| File | Purpose |
|------|---------|
| `src/BoamBridge.cs` | C# bridge: Harmony patches, HTTP calls, unit data gathering |
| `boam_tactical_engine/Program.fs` | HTTP server, endpoint routing, logging |
| `boam_tactical_engine/HeatmapRenderer.fs` | PNG rendering with ImageSharp |
| `boam_tactical_engine/GameTypes.fs` | F# mirror types for game concepts |
| `boam_tactical_engine/Sidecar.fsproj` | .NET 10 project file |
| `boam_asset_pipeline/icon-config.json` | Icon source→output mapping config |
| `boam_asset_pipeline/generate-icons.sh` | Resize game assets to icon PNGs |
| `boam_asset_pipeline/IconGenerator.fs` | Placeholder circle icon generator |

## Next Steps

- [ ] Hook `EvaluateBehaviors`/`PickBehavior` for decision summary (which behavior won, runner-up, score gap)
- [ ] Capture opponent-based behavior evaluations (InflictDamage targets, hit chance, expected damage)
- [ ] Optional per-criterion breakdown (re-enable U/S/D view if needed)
- [ ] Graph engine integration — nodes that modify tile scores based on custom logic
