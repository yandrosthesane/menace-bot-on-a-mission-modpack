/// IconGenerator — produces placeholder icon PNGs for the BOAM heatmap overlay.
/// All icons are white on transparent, tinted at runtime with faction color.
module BOAM.Sidecar.IconGenerator

open System
open System.IO
open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open SixLabors.ImageSharp.Processing
open SixLabors.ImageSharp.Drawing.Processing
open SixLabors.Fonts

let private iconSize = 64
let private white = Rgba32(255uy, 255uy, 255uy, 230uy)

/// Fill a circle (centered, with radius).
let private fillCircle (img: Image<Rgba32>) (cx: int) (cy: int) (r: int) =
    let r2 = r * r
    for y = cy - r to cy + r do
        for x = cx - r to cx + r do
            let dx = x - cx
            let dy = y - cy
            if dx * dx + dy * dy <= r2 && x >= 0 && x < img.Width && y >= 0 && y < img.Height then
                img.[x, y] <- white

/// Fill a diamond (centered).
let private fillDiamond (img: Image<Rgba32>) (cx: int) (cy: int) (r: int) =
    for y = cy - r to cy + r do
        for x = cx - r to cx + r do
            if abs (x - cx) + abs (y - cy) <= r && x >= 0 && x < img.Width && y >= 0 && y < img.Height then
                img.[x, y] <- white

/// Fill an upward triangle (centered).
let private fillTriangleUp (img: Image<Rgba32>) (cx: int) (cy: int) (r: int) =
    let top = cy - r
    let bot = cy + r
    let height = bot - top
    for y = top to bot do
        let progress = float (y - top) / float height
        let halfW = int (float r * progress)
        for x = cx - halfW to cx + halfW do
            if x >= 0 && x < img.Width && y >= 0 && y < img.Height then
                img.[x, y] <- white

/// Fill a downward triangle.
let private fillTriangleDown (img: Image<Rgba32>) (cx: int) (cy: int) (r: int) =
    let top = cy - r
    let bot = cy + r
    let height = bot - top
    for y = top to bot do
        let progress = float (bot - y) / float height
        let halfW = int (float r * progress)
        for x = cx - halfW to cx + halfW do
            if x >= 0 && x < img.Width && y >= 0 && y < img.Height then
                img.[x, y] <- white

/// Fill a square.
let private fillSquare (img: Image<Rgba32>) (cx: int) (cy: int) (r: int) =
    for y = cy - r to cy + r do
        for x = cx - r to cx + r do
            if x >= 0 && x < img.Width && y >= 0 && y < img.Height then
                img.[x, y] <- white

/// Draw a wing shape (two arcs) for flyer.
let private fillWings (img: Image<Rgba32>) (cx: int) (cy: int) (r: int) =
    // Body: small diamond
    fillDiamond img cx cy (r / 3)
    // Left wing: triangle pointing left-up
    for y = cy - r to cy do
        let progress = float (cy - y) / float r
        let w = int (float r * progress * 0.9)
        for x = cx - r to cx - r / 3 + w do
            if x >= 0 && x < img.Width && y >= 0 && y < img.Height then
                img.[x, y] <- white
    // Right wing: mirror
    for y = cy - r to cy do
        let progress = float (cy - y) / float r
        let w = int (float r * progress * 0.9)
        for x = cx + r / 3 - w to cx + r do
            if x >= 0 && x < img.Width && y >= 0 && y < img.Height then
                img.[x, y] <- white

/// Generate a single icon and save it.
let private generateIcon (path: string) (drawFn: Image<Rgba32> -> int -> int -> int -> unit) =
    if File.Exists(path) then () // don't overwrite existing custom icons
    else
        use img = new Image<Rgba32>(iconSize, iconSize)
        let cx = iconSize / 2
        let cy = iconSize / 2
        let r = iconSize / 2 - 4
        drawFn img cx cy r
        img.Save(path)

/// Generate a text-based placeholder icon (for player templates).
let private generateTextIcon (path: string) (text: string) =
    if File.Exists(path) then ()
    else
        use img = new Image<Rgba32>(iconSize, iconSize)
        // Background: rounded-ish square
        let cx = iconSize / 2
        let cy = iconSize / 2
        let r = iconSize / 2 - 3
        fillSquare img cx cy r
        // Overlay dark text
        try
            let fontFamily = SystemFonts.Collection.Get("DejaVu Sans Mono")
            let fontSize = if text.Length <= 2 then 16f elif text.Length <= 4 then 11f else 8f
            let font = fontFamily.CreateFont(fontSize, FontStyle.Bold)
            let opts = RichTextOptions(font)
            opts.HorizontalAlignment <- HorizontalAlignment.Center
            opts.VerticalAlignment <- VerticalAlignment.Center
            opts.Origin <- PointF(float32 cx, float32 cy)
            img.Mutate(fun ctx -> ctx.DrawText(opts, text, Color.Black) |> ignore)
        with _ -> () // font not available — plain square
        img.Save(path)

/// Generate all placeholder icons into the given base directory.
/// Structure: base/archetypes/*.png, base/factions/*.png, base/templates/*.png
let generateAll (baseDir: string) =
    let facDir = Path.Combine(baseDir, "factions")
    let tplDir = Path.Combine(baseDir, "templates")
    Directory.CreateDirectory(facDir) |> ignore
    Directory.CreateDirectory(tplDir) |> ignore

    // Factions: circles (fallback icons, also copied to templates/ for unknown units)
    let factions = [ "neutral"; "player"; "playerai"; "civilian"; "allied"; "enemy_local";
                     "pirates"; "wildlife"; "constructs"; "rogue_army" ]
    for f in factions do
        generateIcon (Path.Combine(facDir, f + ".png")) fillCircle

    // Template placeholders: white square with short code text.
    // Filenames use the full template name after dot prefix (e.g., "alien_stinger.png").
    // Replace these PNGs with real game icons — the sidecar won't regenerate existing ones.
    let knownTemplates = [
        // Wildlife — full names as they appear in game
        "alien_01_small_spiderling"; "alien_01_big_warrior_young"
        "alien_big_blaster_bug"; "alien_stinger"; "alien_dragonfly"
        // Civilian
        "worker"
    ]
    for t in knownTemplates do
        // Short code for placeholder text: last meaningful word, up to 4 chars
        let parts = t.Split('_')
        let lastWord = parts.[parts.Length - 1]
        let code = if lastWord.Length <= 4 then lastWord.ToUpper() else lastWord.Substring(0, 4).ToUpper()
        generateTextIcon (Path.Combine(tplDir, t + ".png")) code
