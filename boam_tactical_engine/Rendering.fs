/// Low-level pixel operations and rendering primitives for heatmap images.
module BOAM.TacticalEngine.Rendering

open System
open System.IO
open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open SixLabors.ImageSharp.Processing
open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.Config

let private cfg = Current.Rendering

/// Map metadata from mapbg.info (texW,texH,tilesX,tilesZ).
type MapInfo = {
    TextureWidth: int
    TextureHeight: int
    TilesX: int
    TilesZ: int
    PixelsPerTile: int
}

/// Border style for tile marker overlays.
type BorderStyle = { Margin: int; Thickness: int; Color: Rgba32 }

let private toBorder (bc: Config.BorderConfig) =
    { Margin = bc.Margin; Thickness = bc.Thickness; Color = Rgba32(bc.Color.[0], bc.Color.[1], bc.Color.[2], bc.Color.[3]) }

let actorBorder    = toBorder cfg.ActorBorder
let bestTileBorder = toBorder cfg.BestTileBorder
let moveDestBorder = toBorder cfg.MoveDestBorder
let visionColor    = Rgba32(cfg.VisionColor.[0], cfg.VisionColor.[1], cfg.VisionColor.[2], cfg.VisionColor.[3])

/// Load mapbg.info from the TacticalMap mod folder.
let loadMapInfo (modFolder: string) : MapInfo option =
    let infoPath = Path.Combine(modFolder, "mapbg.info")
    if not (File.Exists(infoPath)) then None
    else
        let parts = File.ReadAllText(infoPath).Trim().Split(',')
        if parts.Length < 4 then None
        else
            let texW = int parts.[0]
            let texH = int parts.[1]
            let tilesX = int parts.[2]
            let tilesZ = int parts.[3]
            Some {
                TextureWidth = texW
                TextureHeight = texH
                TilesX = tilesX
                TilesZ = tilesZ
                PixelsPerTile = if tilesX > 0 then texW / tilesX else 8
            }

/// Minimum pixels per tile — controls upscaling so text is readable.
let private minTilePixels = cfg.MinTilePixels

/// Prepare a scaled copy of the background image. Returns (image, scaledPixelsPerTile).
let prepareBackground (bgPath: string) (mapInfo: MapInfo) =
    let bg = Image.Load<Rgba32>(bgPath)
    let ppt = mapInfo.PixelsPerTile
    let scale = max 1 (minTilePixels / ppt)
    let scaledPpt = ppt * scale
    if scale > 1 then
        bg.Mutate(fun ctx ->
            ctx.Resize(bg.Width * scale, bg.Height * scale, KnownResamplers.NearestNeighbor) |> ignore)
    // Gamma correction to lift dark pixels (gamma < 1 brightens; 0.35 aggressively lifts shadows)
    let gamma = cfg.Gamma
    bg.ProcessPixelRows(fun accessor ->
        for y = 0 to accessor.Height - 1 do
            let row = accessor.GetRowSpan(y)
            for x = 0 to row.Length - 1 do
                let p = row.[x]
                row.[x] <- Rgba32(
                    byte (MathF.Pow(float32 p.R / 255f, gamma) * 255f),
                    byte (MathF.Pow(float32 p.G / 255f, gamma) * 255f),
                    byte (MathF.Pow(float32 p.B / 255f, gamma) * 255f),
                    p.A))
    bg, scaledPpt

/// Compute the scale factor for a given MapInfo (for stampMoveDestination).
let computeScaledPpt (mapInfo: MapInfo) =
    let ppt = mapInfo.PixelsPerTile
    let scale = max 1 (minTilePixels / ppt)
    ppt * scale

/// Tile top-left pixel coords (Y-flipped: tile z=0 is bottom of image).
let tileOrigin (mapInfo: MapInfo) (scaledPpt: int) (tileX: int) (tileZ: int) =
    let px = float32 (tileX * scaledPpt)
    let py = float32 ((mapInfo.TilesZ - 1 - tileZ) * scaledPpt)
    px, py

/// Draw a border rectangle around a tile with the given style.
let drawTileBorder (bg: Image<Rgba32>) (mapInfo: MapInfo) (scaledPpt: int) (pos: TilePos) (style: BorderStyle) =
    let px, py = tileOrigin mapInfo scaledPpt pos.X pos.Z
    let m = style.Margin
    for i = m to scaledPpt - 1 - m do
        for t = 0 to style.Thickness - 1 do
            // Top edge
            if int py + m + t < bg.Height && int px + i < bg.Width then
                bg.[int px + i, int py + m + t] <- style.Color
            // Bottom edge
            if int py + scaledPpt - 1 - m - t >= 0 && int px + i < bg.Width then
                bg.[int px + i, int py + scaledPpt - 1 - m - t] <- style.Color
            // Left edge
            if int px + m + t < bg.Width && int py + i < bg.Height then
                bg.[int px + m + t, int py + i] <- style.Color
            // Right edge
            if int px + scaledPpt - 1 - m - t >= 0 && int py + i < bg.Height then
                bg.[int px + scaledPpt - 1 - m - t, int py + i] <- style.Color

/// Draw outer-edge borders on a set of tiles (only edges where adjacent tile is NOT in the set).
let drawRangeBorders (bg: Image<Rgba32>) (mapInfo: MapInfo) (scaledPpt: int) (tiles: Set<int * int>) (color: Rgba32) =
    for (tx, tz) in tiles do
        let px, py = tileOrigin mapInfo scaledPpt tx tz
        let x0 = int px
        let y0 = int py
        let hasTop = tiles.Contains(tx, tz + 1)
        let hasBottom = tiles.Contains(tx, tz - 1)
        let hasLeft = tiles.Contains(tx - 1, tz)
        let hasRight = tiles.Contains(tx + 1, tz)
        for i = 0 to scaledPpt - 1 do
            if not hasTop && y0 >= 0 && y0 < bg.Height && x0 + i >= 0 && x0 + i < bg.Width then
                bg.[x0 + i, y0] <- color
            if not hasBottom && y0 + scaledPpt - 1 >= 0 && y0 + scaledPpt - 1 < bg.Height && x0 + i >= 0 && x0 + i < bg.Width then
                bg.[x0 + i, y0 + scaledPpt - 1] <- color
            if not hasLeft && x0 >= 0 && x0 < bg.Width && y0 + i >= 0 && y0 + i < bg.Height then
                bg.[x0, y0 + i] <- color
            if not hasRight && x0 + scaledPpt - 1 >= 0 && x0 + scaledPpt - 1 < bg.Width && y0 + i >= 0 && y0 + i < bg.Height then
                bg.[x0 + scaledPpt - 1, y0 + i] <- color

/// Resize icon to target size.
let resizeIcon (source: Image<Rgba32>) (size: int) =
    let resized = source.Clone()
    if resized.Width <> size || resized.Height <> size then
        resized.Mutate(fun ctx -> ctx.Resize(size, size, KnownResamplers.Lanczos3) |> ignore)
    resized

/// Composite a small icon onto the background at (destX, destY) with alpha blending.
let blitIcon (bg: Image<Rgba32>) (icon: Image<Rgba32>) (destX: int) (destY: int) =
    icon.ProcessPixelRows(bg, fun srcAcc dstAcc ->
        for y = 0 to srcAcc.Height - 1 do
            let dy = destY + y
            if dy >= 0 && dy < dstAcc.Height then
                let srcRow = srcAcc.GetRowSpan(y)
                let dstRow = dstAcc.GetRowSpan(dy)
                for x = 0 to srcRow.Length - 1 do
                    let dx = destX + x
                    if dx >= 0 && dx < dstAcc.Width then
                        let s = srcRow.[x]
                        if s.A > 0uy then
                            let d = dstRow.[dx]
                            let sa = int s.A
                            let da = 255 - sa
                            dstRow.[dx] <- Rgba32(
                                byte ((int s.R * sa + int d.R * da) / 255),
                                byte ((int s.G * sa + int d.G * da) / 255),
                                byte ((int s.B * sa + int d.B * da) / 255),
                                byte (min 255 (sa + int d.A * da / 255))))
