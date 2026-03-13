/// Loads config.json at startup. No hot reload.
module BOAM.Sidecar.Config

open System
open System.IO
open System.Text.Json

type BorderConfig = { Margin: int; Thickness: int; Color: byte array }
type RenderingConfig = {
    MinTilePixels: int
    Gamma: float32
    FontFamily: string
    ScoreFontScale: float32
    LabelFontScale: float32
    ActorBorder: BorderConfig
    BestTileBorder: BorderConfig
    MoveDestBorder: BorderConfig
    VisionColor: byte array
    FactionColors: Map<int, byte array>
}
type SidecarConfig = {
    Port: int
    Rendering: RenderingConfig
}

let private parseBorder (el: JsonElement) : BorderConfig =
    let color = el.GetProperty("color")
    { Margin = el.GetProperty("margin").GetInt32()
      Thickness = el.GetProperty("thickness").GetInt32()
      Color = [| for c in color.EnumerateArray() -> c.GetByte() |] }

let private parseColorArray (el: JsonElement) : byte array =
    [| for c in el.EnumerateArray() -> c.GetByte() |]

let private parseFactionColors (el: JsonElement) : Map<int, byte array> =
    [ for prop in el.EnumerateObject() ->
        int prop.Name, parseColorArray prop.Value ]
    |> Map.ofList

let private load () : SidecarConfig =
    let exeDir = AppContext.BaseDirectory
    let configPath = Path.Combine(exeDir, "config.json")
    // Fallback to source dir (for development / dotnet run)
    let configPath =
        if File.Exists(configPath) then configPath
        else
            let srcDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "config.json") |> Path.GetFullPath
            if File.Exists(srcDir) then srcDir
            else
                // Try CWD
                let cwd = Path.Combine(Environment.CurrentDirectory, "config.json")
                if File.Exists(cwd) then cwd
                else failwithf "config.json not found (checked %s, %s, %s)" (Path.Combine(exeDir, "config.json")) srcDir cwd

    let json = File.ReadAllText(configPath)
    let doc = JsonDocument.Parse(json)
    let root = doc.RootElement
    let r = root.GetProperty("rendering")
    let borders = r.GetProperty("borders")

    { Port = root.GetProperty("port").GetInt32()
      Rendering = {
        MinTilePixels = r.GetProperty("minTilePixels").GetInt32()
        Gamma = r.GetProperty("gamma").GetSingle()
        FontFamily = r.GetProperty("fontFamily").GetString()
        ScoreFontScale = r.GetProperty("scoreFontScale").GetSingle()
        LabelFontScale = r.GetProperty("labelFontScale").GetSingle()
        ActorBorder = parseBorder (borders.GetProperty("actor"))
        BestTileBorder = parseBorder (borders.GetProperty("bestTile"))
        MoveDestBorder = parseBorder (borders.GetProperty("moveDest"))
        VisionColor = parseColorArray (borders.GetProperty("vision"))
        FactionColors = parseFactionColors (r.GetProperty("factionColors"))
      } }

/// Singleton config — loaded once at module init.
let Current = load ()
