/// HeatmapRenderer — overlays per-tile score values on the TacticalMap background.
/// Renders U/S/D scores on each tile in a single image, plus a combined-score-only image.
module BOAM.Sidecar.HeatmapRenderer

open System
open System.IO
open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open SixLabors.ImageSharp.Processing
open SixLabors.ImageSharp.Drawing.Processing
open SixLabors.Fonts

/// A single tile's combined score as received from the bridge.
type TileScoreData = {
    X: int
    Z: int
    Combined: float32
}

/// Map metadata from mapbg.info (texW,texH,tilesX,tilesZ).
type MapInfo = {
    TextureWidth: int
    TextureHeight: int
    TilesX: int
    TilesZ: int
    PixelsPerTile: int
}

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
let private minTilePixels = 64

/// Prepare a scaled copy of the background image. Returns (image, scaledPixelsPerTile).
let private prepareBackground (bgPath: string) (mapInfo: MapInfo) =
    let bg = Image.Load<Rgba32>(bgPath)
    let ppt = mapInfo.PixelsPerTile
    let scale = max 1 (minTilePixels / ppt)
    let scaledPpt = ppt * scale
    if scale > 1 then
        bg.Mutate(fun ctx ->
            ctx.Resize(bg.Width * scale, bg.Height * scale, KnownResamplers.NearestNeighbor) |> ignore)
    // Gamma correction to lift dark pixels (gamma < 1 brightens; 0.35 aggressively lifts shadows)
    let gamma = 0.35f
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

/// Tile top-left pixel coords (Y-flipped: tile z=0 is bottom of image).
let private tileOrigin (mapInfo: MapInfo) (scaledPpt: int) (tileX: int) (tileZ: int) =
    let px = float32 (tileX * scaledPpt)
    let py = float32 ((mapInfo.TilesZ - 1 - tileZ) * scaledPpt)
    px, py

/// Draw a bright marker at the actor's current position.
let private drawActorMarker (bg: Image<Rgba32>) (mapInfo: MapInfo) (scaledPpt: int) (pos: GameTypes.TilePos) =
    let px, py = tileOrigin mapInfo scaledPpt pos.X pos.Z
    let margin = 2
    let markerColor = Rgba32(255uy, 50uy, 50uy, 220uy)
    // Draw border rectangle around the actor's tile
    for i = margin to scaledPpt - 1 - margin do
        for thickness = 0 to 2 do
            let t = float32 thickness
            // Top edge
            if int py + margin + int t < bg.Height && int px + i < bg.Width then
                bg.[int px + i, int py + margin + int t] <- markerColor
            // Bottom edge
            if int py + scaledPpt - 1 - margin - int t >= 0 && int px + i < bg.Width then
                bg.[int px + i, int py + scaledPpt - 1 - margin - int t] <- markerColor
            // Left edge
            if int px + margin + int t < bg.Width && int py + i < bg.Height then
                bg.[int px + margin + int t, int py + i] <- markerColor
            // Right edge
            if int px + scaledPpt - 1 - margin - int t >= 0 && int py + i < bg.Height then
                bg.[int px + scaledPpt - 1 - margin - int t, int py + i] <- markerColor

/// Faction index → faction icon filename.
let private factionIconName (factionIdx: int) =
    match factionIdx with
    | 0 -> "neutral" | 1 -> "player" | 2 -> "playerai" | 3 -> "civilian"
    | 4 -> "allied" | 5 -> "enemy_local" | 6 -> "pirates" | 7 -> "wildlife"
    | 8 -> "constructs" | 9 -> "rogue_army" | _ -> "neutral"

/// Icon cache to avoid reloading PNGs every frame (concurrent-safe).
let private iconCache = Collections.Concurrent.ConcurrentDictionary<string, Image<Rgba32> option>()

/// Try to load a PNG, returning None if not found. Caches results.
let private tryLoadIcon (path: string) =
    iconCache.GetOrAdd(path, fun p ->
        if File.Exists(p) then Some (Image.Load<Rgba32>(p))
        else None)

/// Full template name after dot prefix (e.g., "enemy.alien_stinger" → "alien_stinger").
/// Used for icon file lookup — no stopword stripping.
let private templateFileName (name: string) =
    if String.IsNullOrEmpty(name) then ""
    else
        match name.LastIndexOf('.') with
        | -1 -> name
        | i -> name.Substring(i + 1)

/// Copy a fallback icon to the template path so the user has the correct filename to replace.
let private copyFallback (sourcePath: string) (tplPath: string) =
    try
        let dir = Path.GetDirectoryName(tplPath)
        Directory.CreateDirectory(dir) |> ignore
        File.Copy(sourcePath, tplPath, false) // don't overwrite existing
    with _ -> () // race condition or permission — ignore

/// Resolve icon for a unit: leader → template → faction fallback.
/// When template icon is missing, copies the faction icon into templates/ with the correct filename.
let private resolveIcon (iconBaseDir: string) (fullName: string) (leaderName: string) (factionIdx: int) =
    let tplDir = Path.Combine(iconBaseDir, "templates")
    let tplName = templateFileName fullName
    let tplPath = if tplName <> "" then Path.Combine(tplDir, tplName + ".png") else ""

    // 0. Leader-specific icon (e.g. "rewa.png")
    let leaderPath = if String.IsNullOrEmpty(leaderName) then "" else Path.Combine(tplDir, leaderName.ToLower() + ".png")
    match (if leaderPath <> "" then tryLoadIcon leaderPath else None) with
    | Some img -> Some img
    | None ->

    // 1. Template-specific icon
    match (if tplPath <> "" then tryLoadIcon tplPath else None) with
    | Some img -> Some img
    | None ->

    // 2. Faction fallback — copy to template path and leader path so user has the right filenames to replace
    let facPath = Path.Combine(iconBaseDir, "factions", factionIconName factionIdx + ".png")
    match tryLoadIcon facPath with
    | Some img ->
        if tplPath <> "" then copyFallback facPath tplPath
        if leaderPath <> "" then copyFallback facPath leaderPath
        Some img
    | None -> None

/// Tint a white icon with a faction color — returns a new copy.
/// Resize icon to target size (no color tinting — badges have baked-in faction colors).
let private resizeIcon (source: Image<Rgba32>) (size: int) =
    let resized = source.Clone()
    if resized.Width <> size || resized.Height <> size then
        resized.Mutate(fun ctx -> ctx.Resize(size, size, KnownResamplers.Lanczos3) |> ignore)
    resized

/// Composite a small icon onto the background at (destX, destY).
let private blitIcon (bg: Image<Rgba32>) (icon: Image<Rgba32>) (destX: int) (destY: int) =
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

/// Faction colors matching TacticalMap conventions.
let private factionColor (factionIdx: int) =
    match factionIdx with
    | 1 -> Rgba32(60uy, 140uy, 255uy, 200uy)   // Player — blue
    | 2 -> Rgba32(80uy, 160uy, 255uy, 200uy)   // PlayerAI — light blue
    | 3 -> Rgba32(200uy, 200uy, 100uy, 200uy)  // Civilian — yellow
    | 4 -> Rgba32(100uy, 200uy, 100uy, 200uy)  // AlliedLocalForces — green
    | 5 -> Rgba32(200uy, 100uy, 50uy, 200uy)   // EnemyLocalForces — orange
    | 6 -> Rgba32(180uy, 50uy, 180uy, 200uy)   // Pirates — purple
    | 7 -> Rgba32(255uy, 60uy, 60uy, 200uy)    // Wildlife — red
    | 8 -> Rgba32(160uy, 160uy, 160uy, 200uy)  // Constructs — gray
    | 9 -> Rgba32(200uy, 50uy, 50uy, 200uy)    // RogueArmy — dark red
    | _ -> Rgba32(128uy, 128uy, 128uy, 200uy)  // Unknown — gray

/// Faction prefix for unit labels.
let private factionPrefix (factionIdx: int) =
    match factionIdx with
    | 1 -> "P" | 2 -> "PA" | 3 -> "C" | 4 -> "A"
    | 5 -> "E" | 6 -> "Pi" | 7 -> "W" | 8 -> "Co" | 9 -> "R"
    | _ -> "?"

/// Noise words to strip from template names for compact labels.
let private stopwords =
    Set.ofList [ "alien"; "soldier"; "civilian"; "enemy"; "small"; "big"; "light"; "heavy"; "comp"; "01"; "02"; "03"; "04"; "05" ]

/// Short name from template name: strip prefix, drop stopwords, keep last 2 segments.
let private shortName (name: string) =
    if System.String.IsNullOrEmpty(name) then ""
    else
        // Strip "enemy.", "civilian." etc. prefix
        let afterDot = match name.LastIndexOf('.') with | -1 -> name | i -> name.Substring(i + 1)
        let segments = afterDot.Split('_') |> Array.filter (fun s -> not (stopwords.Contains(s)))
        if segments.Length = 0 then
            // Fallback: last segment of original
            let parts = afterDot.Split('_')
            parts.[parts.Length - 1]
        elif segments.Length <= 2 then
            System.String.Join("_", segments)
        else
            // Keep last 2 meaningful segments
            System.String.Join("_", segments.[segments.Length - 2 ..])

/// Display name for a unit: prefer leader nickname, fallback to template shortName.
let private unitDisplayName (u: GameTypes.UnitInfo) =
    if not (System.String.IsNullOrEmpty(u.Leader)) then u.Leader.ToLower()
    else shortName u.Name

/// Build unit labels: assign per-faction index, produce "W_stinger_1" or "P_rewa_1" style labels.
let private buildUnitLabels (units: GameTypes.UnitInfo list) =
    let counters = System.Collections.Generic.Dictionary<int, int>()
    units |> List.map (fun u ->
        let idx =
            match counters.TryGetValue(u.Faction) with
            | true, n -> counters.[u.Faction] <- n + 1; n + 1
            | _ -> counters.[u.Faction] <- 1; 1
        let prefix = factionPrefix u.Faction
        let dn = unitDisplayName u
        let label = if dn = "" then sprintf "%s_%d" prefix idx else sprintf "%s_%s_%d" prefix dn idx
        u, label)

/// Draw icons for all units, with labels only for enemies + the analyzed actor.
/// Uses icon fallback chain: template → archetype → faction → colored square.
/// Returns the label assigned to the actor at actorPos (for use in filename).
let private drawUnits
    (bg: Image<Rgba32>) (mapInfo: MapInfo) (scaledPpt: int)
    (units: GameTypes.UnitInfo list) (currentFaction: int)
    (actorPos: GameTypes.TilePos option) (font: Font) (iconBaseDir: string)
    : string option =
    let iconSize = scaledPpt
    let offset = (scaledPpt - iconSize) / 2  // center icon within tile; 0 when icon == tile size
    let labeled = buildUnitLabels units
    let opts = RichTextOptions(font)
    let mutable actorLabel = None
    for (unit, label) in labeled do
        let px, py = tileOrigin mapInfo scaledPpt unit.Position.X unit.Position.Z
        let color = factionColor unit.Faction
        let startX = int px + offset
        let startY = int py + offset
        // Try icon fallback chain: leader → template → faction
        match resolveIcon iconBaseDir unit.Name unit.Leader unit.Faction with
        | Some srcIcon ->
            use tinted = resizeIcon srcIcon iconSize
            blitIcon bg tinted startX startY
        | None ->
            // Hard fallback: colored square
            for dy = 0 to iconSize - 1 do
                for dx = 0 to iconSize - 1 do
                    let ix = startX + dx
                    let iy = startY + dy
                    if ix >= 0 && ix < bg.Width && iy >= 0 && iy < bg.Height then
                        bg.[ix, iy] <- color
        // Check if this unit is the analyzed actor
        let isAnalyzedActor =
            match actorPos with
            | Some ap -> unit.Position.X = ap.X && unit.Position.Z = ap.Z && unit.Faction = currentFaction
            | None -> false
        if isAnalyzedActor then actorLabel <- Some label
        // Show label for: enemies (different faction) OR the analyzed actor itself
        let isEnemy = unit.Faction <> currentFaction
        if isEnemy || isAnalyzedActor then
            let cx = float32 (int px) + float32 scaledPpt / 2f
            let labelY = float32 (int py + offset + iconSize + 2)
            opts.Origin <- PointF(cx, labelY)
            opts.HorizontalAlignment <- HorizontalAlignment.Center
            opts.VerticalAlignment <- VerticalAlignment.Top
            let labelColor = Color.FromPixel(color)
            bg.Mutate(fun ctx -> ctx.DrawText(opts, label, labelColor) |> ignore)
    actorLabel

/// Compact number format — uses k suffix for thousands.
let private compactNum (v: float32) =
    let a = abs v
    if a >= 1000f then sprintf "%.1fk" (v / 1000f)
    elif a >= 10f then sprintf "%.0f" v
    else sprintf "%.1f" v

/// Draw a green border on the best-scoring tile (highest combined).
let private drawBestTileMarker (bg: Image<Rgba32>) (mapInfo: MapInfo) (scaledPpt: int) (pos: GameTypes.TilePos) =
    let px, py = tileOrigin mapInfo scaledPpt pos.X pos.Z
    let margin = 1
    let color = Rgba32(50uy, 255uy, 50uy, 230uy)
    for i = margin to scaledPpt - 1 - margin do
        for thickness = 0 to 1 do
            let t = float32 thickness
            if int py + margin + int t < bg.Height && int px + i < bg.Width then
                bg.[int px + i, int py + margin + int t] <- color
            if int py + scaledPpt - 1 - margin - int t >= 0 && int px + i < bg.Width then
                bg.[int px + i, int py + scaledPpt - 1 - margin - int t] <- color
            if int px + margin + int t < bg.Width && int py + i < bg.Height then
                bg.[int px + margin + int t, int py + i] <- color
            if int px + scaledPpt - 1 - margin - int t >= 0 && int py + i < bg.Height then
                bg.[int px + scaledPpt - 1 - margin - int t, int py + i] <- color

/// Draw a thin border on each tile in a set — used for movement/vision range overlays.
/// Only draws the OUTER edges (edges where the adjacent tile is NOT in the set).
let private drawRangeBorders (bg: Image<Rgba32>) (mapInfo: MapInfo) (scaledPpt: int) (tiles: Set<int * int>) (color: Rgba32) =
    for (tx, tz) in tiles do
        let px, py = tileOrigin mapInfo scaledPpt tx tz
        let x0 = int px
        let y0 = int py
        let hasTop = tiles.Contains(tx, tz + 1)
        let hasBottom = tiles.Contains(tx, tz - 1)
        let hasLeft = tiles.Contains(tx - 1, tz)
        let hasRight = tiles.Contains(tx + 1, tz)
        // Draw edges where neighbor is NOT in the set
        for i = 0 to scaledPpt - 1 do
            if not hasTop && y0 >= 0 && y0 < bg.Height && x0 + i >= 0 && x0 + i < bg.Width then
                bg.[x0 + i, y0] <- color
            if not hasBottom && y0 + scaledPpt - 1 >= 0 && y0 + scaledPpt - 1 < bg.Height && x0 + i >= 0 && x0 + i < bg.Width then
                bg.[x0 + i, y0 + scaledPpt - 1] <- color
            if not hasLeft && x0 >= 0 && x0 < bg.Width && y0 + i >= 0 && y0 + i < bg.Height then
                bg.[x0, y0 + i] <- color
            if not hasRight && x0 + scaledPpt - 1 >= 0 && x0 + scaledPpt - 1 < bg.Width && y0 + i >= 0 && y0 + i < bg.Height then
                bg.[x0 + scaledPpt - 1, y0 + i] <- color

/// Draw a blue border on the tile the unit actually reached after movement.
let private drawMoveDestMarker (bg: Image<Rgba32>) (mapInfo: MapInfo) (scaledPpt: int) (pos: GameTypes.TilePos) =
    let px, py = tileOrigin mapInfo scaledPpt pos.X pos.Z
    let margin = 3
    let color = Rgba32(60uy, 140uy, 255uy, 230uy)
    for i = margin to scaledPpt - 1 - margin do
        for thickness = 0 to 1 do
            let t = float32 thickness
            if int py + margin + int t < bg.Height && int px + i < bg.Width then
                bg.[int px + i, int py + margin + int t] <- color
            if int py + scaledPpt - 1 - margin - int t >= 0 && int px + i < bg.Width then
                bg.[int px + i, int py + scaledPpt - 1 - margin - int t] <- color
            if int px + margin + int t < bg.Width && int py + i < bg.Height then
                bg.[int px + margin + int t, int py + i] <- color
            if int px + scaledPpt - 1 - margin - int t >= 0 && int py + i < bg.Height then
                bg.[int px + scaledPpt - 1 - margin - int t, int py + i] <- color

/// Find the tile with the highest combined score.
let private bestTile (tiles: TileScoreData list) =
    tiles |> List.maxBy (fun t -> t.Combined)

/// Render the combined score image: single value per tile, with unit overlay.
let renderCombined
    (bgPath: string)
    (mapInfo: MapInfo)
    (tiles: TileScoreData list)
    (actorPos: GameTypes.TilePos option)
    (units: GameTypes.UnitInfo list)
    (currentFaction: int)
    (iconBaseDir: string)
    (outputDir: string)
    (label: string)
    (actorId: int)
    (visionRange: int)
    : string * string option =

    let bg, scaledPpt = prepareBackground bgPath mapInfo

    let fontFamily = SystemFonts.Collection.Get("DejaVu Sans Mono")
    let fontSize = float32 scaledPpt * 0.32f
    let font = fontFamily.CreateFont(fontSize, FontStyle.Bold)
    let labelFontSize = float32 scaledPpt * 0.33f
    let labelFont = fontFamily.CreateFont(labelFontSize, FontStyle.Bold)

    // Draw unit markers first (behind score text); returns analyzed actor's label
    let actorLabel = drawUnits bg mapInfo scaledPpt units currentFaction actorPos labelFont iconBaseDir

    let opts = RichTextOptions(font)
    opts.HorizontalAlignment <- HorizontalAlignment.Center
    opts.VerticalAlignment <- VerticalAlignment.Center

    for tile in tiles do
        let px, py = tileOrigin mapInfo scaledPpt tile.X tile.Z
        let cx = px + float32 scaledPpt / 2f
        let cy = py + float32 scaledPpt / 2f

        opts.Origin <- PointF(cx, cy)
        let text = compactNum tile.Combined
        bg.Mutate(fun ctx -> ctx.DrawText(opts, text, Color.White) |> ignore)

    // Vision range border (yellow) — computed as tiles within radius of actor
    match actorPos with
    | Some ap when visionRange > 0 ->
        let visionTiles =
            [ for dx = -visionRange to visionRange do
                for dz = -visionRange to visionRange do
                    let dist = abs dx + abs dz  // Manhattan distance
                    if dist <= visionRange then
                        yield (ap.X + dx, ap.Z + dz) ]
            |> Set.ofList
        drawRangeBorders bg mapInfo scaledPpt visionTiles (Rgba32(255uy, 220uy, 50uy, 200uy))
    | _ -> ()

    // Actor marker (red border) for the currently analyzed unit
    actorPos |> Option.iter (fun pos -> drawActorMarker bg mapInfo scaledPpt pos)

    if not (List.isEmpty tiles) then
        let best = bestTile tiles
        drawBestTileMarker bg mapInfo scaledPpt { X = best.X; Z = best.Z }

    // Filename uses caller-provided label (contains actorId for consistent debugging)
    let outPath = Path.Combine(outputDir, sprintf "%s.png" label)
    bg.Save(outPath)
    bg.Dispose()
    outPath, actorLabel

/// Stamp a blue move-destination marker onto an existing heatmap PNG.
/// Called after movement-finished fires, so the heatmap already exists.
let stampMoveDestination (modFolder: string) (heatmapPath: string) (dest: GameTypes.TilePos) =
    match loadMapInfo modFolder with
    | None -> failwithf "Cannot read mapbg.info from %s" modFolder
    | Some mapInfo ->
        let ppt = mapInfo.PixelsPerTile
        let scale = max 1 (minTilePixels / ppt)
        let scaledPpt = ppt * scale
        use img = Image.Load<Rgba32>(heatmapPath)
        drawMoveDestMarker img mapInfo scaledPpt dest
        img.Save(heatmapPath)

/// Render heatmap with unit overlay.
/// Returns list of (imageName, filePath).
let renderAll
    (modFolder: string)
    (tiles: TileScoreData list)
    (actorPos: GameTypes.TilePos option)
    (units: GameTypes.UnitInfo list)
    (currentFaction: int)
    (iconBaseDir: string)
    (outputDir: string)
    (label: string)
    (actorId: int)
    (visionRange: int)
    : (string * string) list =

    let bgPath = Path.Combine(modFolder, "mapbg.png")
    if not (File.Exists(bgPath)) then
        failwithf "Map background not found: %s" bgPath

    match loadMapInfo modFolder with
    | None -> failwithf "Cannot read mapbg.info from %s" modFolder
    | Some mapInfo ->

    Directory.CreateDirectory(outputDir) |> ignore

    let combined, _ = renderCombined bgPath mapInfo tiles actorPos units currentFaction iconBaseDir outputDir label actorId visionRange

    [ "combined", combined ]
