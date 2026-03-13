/// HeatmapRenderer — overlays per-tile score values on the TacticalMap background.
/// Renders combined score on each tile in a single image.
module BOAM.TacticalEngine.HeatmapRenderer

open System
open System.IO
open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open SixLabors.ImageSharp.Processing
open SixLabors.ImageSharp.Drawing.Processing
open SixLabors.Fonts
open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.Config
open BOAM.TacticalEngine.Rendering
open BOAM.TacticalEngine.Naming
open BOAM.TacticalEngine.FactionTheme

let private cfg = Current.Rendering

/// Icon cache to avoid reloading PNGs every frame (concurrent-safe).
let private iconCache = Collections.Concurrent.ConcurrentDictionary<string, Image<Rgba32> option>()

/// Try to load a PNG, returning None if not found. Caches results.
let private tryLoadIcon (path: string) =
    iconCache.GetOrAdd(path, fun p ->
        if File.Exists(p) then Some (Image.Load<Rgba32>(p))
        else None)

/// Copy a fallback icon to the template path so the user has the correct filename to replace.
let private copyFallback (sourcePath: string) (tplPath: string) =
    try
        let dir = Path.GetDirectoryName(tplPath)
        Directory.CreateDirectory(dir) |> ignore
        File.Copy(sourcePath, tplPath, false)
    with _ -> ()

/// Resolve icon for a unit: leader → template → faction fallback.
let private resolveIcon (iconBaseDir: string) (fullName: string) (leaderName: string) (factionIdx: int) =
    let tplDir = Path.Combine(iconBaseDir, "templates")
    let tplName = templateFileName fullName
    let tplPath = if tplName <> "" then Path.Combine(tplDir, tplName + ".png") else ""

    let leaderPath = if String.IsNullOrEmpty(leaderName) then "" else Path.Combine(tplDir, leaderName.ToLower() + ".png")
    match (if leaderPath <> "" then tryLoadIcon leaderPath else None) with
    | Some img -> Some img
    | None ->

    match (if tplPath <> "" then tryLoadIcon tplPath else None) with
    | Some img -> Some img
    | None ->

    let facPath = Path.Combine(iconBaseDir, "factions", factionIconName factionIdx + ".png")
    match tryLoadIcon facPath with
    | Some img ->
        if tplPath <> "" then copyFallback facPath tplPath
        if leaderPath <> "" then copyFallback facPath leaderPath
        Some img
    | None -> None

/// Draw icons for all units, with labels only for enemies + the analyzed actor.
/// Returns the label assigned to the actor at actorPos (for use in filename).
let private drawUnits
    (bg: Image<Rgba32>) (mapInfo: MapInfo) (scaledPpt: int)
    (units: UnitInfo list) (currentFaction: int)
    (actorPos: TilePos option) (font: Font) (iconBaseDir: string)
    : string option =
    let iconSize = scaledPpt
    let offset = (scaledPpt - iconSize) / 2
    let labeled = buildUnitLabels units
    let opts = RichTextOptions(font)
    let mutable actorLabel = None
    for (unit, label) in labeled do
        let px, py = tileOrigin mapInfo scaledPpt unit.Position.X unit.Position.Z
        let color = factionColor unit.Faction
        let startX = int px + offset
        let startY = int py + offset
        match resolveIcon iconBaseDir unit.Name unit.Leader unit.Faction with
        | Some srcIcon ->
            use tinted = resizeIcon srcIcon iconSize
            blitIcon bg tinted startX startY
        | None ->
            for dy = 0 to iconSize - 1 do
                for dx = 0 to iconSize - 1 do
                    let ix = startX + dx
                    let iy = startY + dy
                    if ix >= 0 && ix < bg.Width && iy >= 0 && iy < bg.Height then
                        bg.[ix, iy] <- color
        let isAnalyzedActor =
            match actorPos with
            | Some ap -> unit.Position.X = ap.X && unit.Position.Z = ap.Z && unit.Faction = currentFaction
            | None -> false
        if isAnalyzedActor then actorLabel <- Some label
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

/// Find the tile with the highest combined score.
let private bestTile (tiles: TileScoreData list) =
    tiles |> List.maxBy (fun t -> t.Combined)

/// Render the combined score image: single value per tile, with unit overlay.
let renderCombined
    (bgPath: string)
    (mapInfo: MapInfo)
    (tiles: TileScoreData list)
    (actorPos: TilePos option)
    (units: UnitInfo list)
    (currentFaction: int)
    (iconBaseDir: string)
    (outputDir: string)
    (label: string)
    (actorId: int)
    (visionRange: int)
    : string * string option =

    let bg, scaledPpt = prepareBackground bgPath mapInfo

    let fontFamily = SystemFonts.Collection.Get(cfg.FontFamily)
    let fontSize = float32 scaledPpt * cfg.ScoreFontScale
    let font = fontFamily.CreateFont(fontSize, FontStyle.Bold)
    let labelFontSize = float32 scaledPpt * cfg.LabelFontScale
    let labelFont = fontFamily.CreateFont(labelFontSize, FontStyle.Bold)

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

    // Vision range border (yellow)
    match actorPos with
    | Some ap when visionRange > 0 ->
        let visionTiles =
            [ for dx = -visionRange to visionRange do
                for dz = -visionRange to visionRange do
                    let dist = abs dx + abs dz
                    if dist <= visionRange then
                        yield (ap.X + dx, ap.Z + dz) ]
            |> Set.ofList
        drawRangeBorders bg mapInfo scaledPpt visionTiles visionColor
    | _ -> ()

    // Actor marker (red border)
    actorPos |> Option.iter (fun pos -> drawTileBorder bg mapInfo scaledPpt pos actorBorder)

    if not (List.isEmpty tiles) then
        let best = bestTile tiles
        drawTileBorder bg mapInfo scaledPpt { X = best.X; Z = best.Z } bestTileBorder

    let outPath = Path.Combine(outputDir, sprintf "%s.png" label)
    bg.Save(outPath)
    bg.Dispose()
    outPath, actorLabel

/// Stamp a blue move-destination marker onto an existing heatmap PNG.
let stampMoveDestination (modFolder: string) (heatmapPath: string) (dest: TilePos) =
    match loadMapInfo modFolder with
    | None -> failwithf "Cannot read mapbg.info from %s" modFolder
    | Some mapInfo ->
        let scaledPpt = computeScaledPpt mapInfo
        use img = Image.Load<Rgba32>(heatmapPath)
        drawTileBorder img mapInfo scaledPpt dest moveDestBorder
        img.Save(heatmapPath)

/// Render heatmap with unit overlay.
/// Returns list of (imageName, filePath).
let renderAll
    (modFolder: string)
    (tiles: TileScoreData list)
    (actorPos: TilePos option)
    (units: UnitInfo list)
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
