# Step 6: C# Bridge Refactoring + Unified Border Rendering

**Date:** 2026-03-13
**Status:** COMPLETE

## Overview

Two related cleanups: unified the three near-identical tile border drawing functions in F#, and extracted shared actor info extraction in the C# bridge.

## Unified Border Rendering

### Problem
Three functions in HeatmapRenderer.fs were copy-pasted with only margin, thickness, and color differing:
- `drawActorMarker` — margin 2, thickness 3, red
- `drawBestTileMarker` — margin 1, thickness 2, green
- `drawMoveDestMarker` — margin 3, thickness 2, blue

### Solution
Single `drawTileBorder` function with a `BorderStyle` record:

```fsharp
type BorderStyle = { Margin: int; Thickness: int; Color: Rgba32 }

let actorBorder    = { Margin = 2; Thickness = 3; Color = Rgba32(255uy, 50uy, 50uy, 220uy) }
let bestTileBorder = { Margin = 1; Thickness = 2; Color = Rgba32(50uy, 255uy, 50uy, 230uy) }
let moveDestBorder = { Margin = 3; Thickness = 2; Color = Rgba32(60uy, 140uy, 255uy, 230uy) }

let drawTileBorder (bg: Image<Rgba32>) (mapInfo: MapInfo) (scaledPpt: int) (pos: TilePos) (style: BorderStyle) = ...
```

Callsites simplified:
```fsharp
// Before
drawActorMarker bg mapInfo scaledPpt pos
drawBestTileMarker bg mapInfo scaledPpt { X = best.X; Z = best.Z }

// After
drawTileBorder bg mapInfo scaledPpt pos actorBorder
drawTileBorder bg mapInfo scaledPpt { X = best.X; Z = best.Z } bestTileBorder
```

`stampMoveDestination` also simplified — no longer duplicates the scale computation:
```fsharp
let scaledPpt = computeScaledPpt mapInfo
drawTileBorder img mapInfo scaledPpt dest moveDestBorder
```

## C# Bridge Helper Extraction

### Problem
All three Harmony patches (`Patch_OnTurnStart`, `Patch_PostProcessTileScores`, `Patch_MovementFinished`) duplicated the same actor info extraction:
```csharp
var gameObj = new GameObj(actor.Pointer);
var info = EntitySpawner.GetEntityInfo(gameObj);
var tplObj = gameObj.ReadObj("m_Template");
var templateName = tplObj.IsNull ? "" : tplObj.GetName() ?? "";
```

And position extraction:
```csharp
var pos = EntityMovement.GetPosition(gameObj);
int x = 0, z = 0;
if (pos.HasValue) { x = pos.Value.x; z = pos.Value.y; }
```

### Solution
Two helpers on `BoamBridge`:

```csharp
internal static (GameObj gameObj, int factionId, int entityId, string templateName)?
    GetActorInfo(Actor actor)

internal static (int x, int z) GetPos(GameObj gameObj)
```

Patches now use:
```csharp
var info = BoamBridge.GetActorInfo(actor);
if (info == null) return;
var (gameObj, factionId, entityId, templateName) = info.Value;
var (x, z) = BoamBridge.GetPos(gameObj);
```

**Note:** `GetActorInfo` is only used where we have a real `Actor` reference. The unit list gathering in `Patch_PostProcessTileScores` still uses the original `GameObj` pattern directly, because `EntitySpawner.ListEntities` returns raw GameObjs that may not be safely castable to `Actor`.

## Files Changed

| File | Change |
|------|--------|
| `Rendering.fs` | `BorderStyle` type, `drawTileBorder`, predefined styles |
| `HeatmapRenderer.fs` | Replaced 3 marker calls with `drawTileBorder` |
| `BoamBridge.cs` | Added `GetActorInfo` + `GetPos` helpers, simplified 3 patches |
