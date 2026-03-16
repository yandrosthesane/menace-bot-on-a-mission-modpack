/// Loads config.json5 at startup. No hot reload.
module BOAM.TacticalEngine.Config

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
type TacticalEngineConfig = {
    Port: int
    BridgePort: int
    CommandPort: int
    Heatmaps: bool
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

/// Strip // and /* */ comments from JSON5 so System.Text.Json can parse it.
let private stripComments (input: string) =
    let sb = System.Text.StringBuilder(input.Length)
    let mutable i = 0
    while i < input.Length do
        if input.[i] = '"' then
            sb.Append('"') |> ignore
            i <- i + 1
            while i < input.Length && input.[i] <> '"' do
                if input.[i] = '\\' && i + 1 < input.Length then
                    sb.Append(input.[i]).Append(input.[i + 1]) |> ignore
                    i <- i + 2
                else
                    sb.Append(input.[i]) |> ignore
                    i <- i + 1
            if i < input.Length then sb.Append('"') |> ignore; i <- i + 1
        elif i + 1 < input.Length && input.[i] = '/' && input.[i + 1] = '/' then
            while i < input.Length && input.[i] <> '\n' do i <- i + 1
        elif i + 1 < input.Length && input.[i] = '/' && input.[i + 1] = '*' then
            i <- i + 2
            while i + 1 < input.Length && not (input.[i] = '*' && input.[i + 1] = '/') do i <- i + 1
            if i + 1 < input.Length then i <- i + 2
        else
            sb.Append(input.[i]) |> ignore
            i <- i + 1
    sb.ToString()

/// Read configVersion from a JSON5 file (0 if missing or unreadable).
let private readVersion (path: string) =
    try
        let doc = JsonDocument.Parse(stripComments (File.ReadAllText(path)))
        match doc.RootElement.TryGetProperty("configVersion") with
        | true, v -> v.GetInt32()
        | _ -> 0
    with _ -> 0

/// Resolve config path: user persistent config → mod default → source dev fallback.
let private resolveConfigPath () =
    let exeDir = AppContext.BaseDirectory
    let gameDir =
        Environment.GetEnvironmentVariable("MENACE_GAME_DIR")
        |> Option.ofObj
        |> Option.defaultValue (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".steam/steam/steamapps/common/Menace"))

    // User persistent config (survives deploys)
    let persistentDir =
        Environment.GetEnvironmentVariable("BOAM_PERSISTENT_ASSETS")
        |> Option.ofObj
        |> Option.defaultValue (Path.Combine(gameDir, "UserData", "BOAM"))
    let userPath = Path.Combine(persistentDir, "configs", "config.json5")

    // Mod default (configs/ next to the engine binary's parent — i.e. Mods/BOAM/configs/)
    let modDir = Path.GetDirectoryName(exeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
    let defaultPath = Path.Combine(modDir, "configs", "config.json5")

    // Pick the best config: user (if version ok) → default
    if File.Exists(userPath) then
        let userVer = readVersion userPath
        let defaultVer = if File.Exists(defaultPath) then readVersion defaultPath else 0
        if userVer >= defaultVer then
            eprintfn "[Config] Using user config (v%d): %s" userVer userPath
            userPath
        else
            eprintfn "[Config] User config outdated (v%d < v%d), using default: %s" userVer defaultVer defaultPath
            defaultPath
    elif File.Exists(defaultPath) then defaultPath
    else failwithf "config.json5 not found (checked %s, %s)" userPath defaultPath

let private load () : TacticalEngineConfig =
    let configPath = resolveConfigPath ()

    let json = stripComments (File.ReadAllText(configPath))
    let doc = JsonDocument.Parse(json)
    let root = doc.RootElement
    let r = root.GetProperty("rendering")
    let borders = r.GetProperty("borders")

    { Port = root.GetProperty("port").GetInt32()
      BridgePort = match root.TryGetProperty("bridge_port") with | true, v -> v.GetInt32() | _ -> 7655
      CommandPort = match root.TryGetProperty("command_port") with | true, v -> v.GetInt32() | _ -> 7661
      Heatmaps = match root.TryGetProperty("heatmaps") with | true, v -> v.GetBoolean() | _ -> true
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
