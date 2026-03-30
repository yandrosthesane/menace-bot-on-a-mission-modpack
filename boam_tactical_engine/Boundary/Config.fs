/// Loads engine.json5 at startup. No hot reload.
module BOAM.TacticalEngine.Config

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.Json

/// Resolve the game install directory. Lookup chain:
///   1. MENACE_GAME_DIR env var
///   2. Platform-specific Steam paths (first that exists)
/// Fails if none found.
let private resolveGameDir () =
    match Environment.GetEnvironmentVariable("MENACE_GAME_DIR") |> Option.ofObj with
    | Some dir when Directory.Exists(dir) -> dir
    | _ ->
        let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        let candidates =
            if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
                [ Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                               "Steam", "steamapps", "common", "Menace") ]
            else
                [ Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "Menace")
                  Path.Combine(home, ".steam", "steam", "steamapps", "common", "Menace") ]
        match candidates |> List.tryFind Directory.Exists with
        | Some dir -> dir
        | None ->
            let searched = candidates |> String.concat ", "
            failwithf "Game directory not found. Set MENACE_GAME_DIR or install to a standard Steam path. Searched: %s" searched

/// Game install directory — resolved once at startup.
let GameDir = resolveGameDir ()

/// Mod directory: {GameDir}/Mods/BOAM
let ModDir = Path.Combine(GameDir, "Mods", "BOAM")

/// User-persistent data directory: {GameDir}/UserData/BOAM
let PersistentDir = Path.Combine(GameDir, "UserData", "BOAM")

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
    Rendering: RenderingConfig
}

// --- Behaviour config (separate file: behaviour.json5) ---

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
let stripComments (input: string) =
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

/// Config source info for logging.
type ConfigSource = { Path: string; Label: string; Version: int }

/// Resolve config path: user persistent config → mod default → source dev fallback.
/// Returns the resolved path and source metadata.
/// Seeds the user config from mod default if it doesn't exist yet.
let private resolveConfigPath () =
    let userPath = Path.Combine(PersistentDir, "configs", "engine.json5")
    let defaultPath = Path.Combine(ModDir, "configs", "engine.json5")

    // Seed user config from mod default if missing
    if not (File.Exists(userPath)) && File.Exists(defaultPath) then
        try
            Directory.CreateDirectory(Path.GetDirectoryName(userPath)) |> ignore
            File.Copy(defaultPath, userPath)
            eprintfn "[Config] Seeded user config from mod default: %s" userPath
        with ex ->
            eprintfn "[Config] Failed to seed user config: %s" ex.Message

    // Pick the best config: user (if version ok) → default
    if File.Exists(userPath) then
        let userVer = readVersion userPath
        let defaultVer = if File.Exists(defaultPath) then readVersion defaultPath else 0
        if userVer >= defaultVer then
            { Path = userPath; Label = "user"; Version = userVer }
        else
            eprintfn "[Config] User config outdated (v%d < v%d), falling back to mod default" userVer defaultVer
            { Path = defaultPath; Label = "default"; Version = defaultVer }
    elif File.Exists(defaultPath) then
        { Path = defaultPath; Label = "default"; Version = readVersion defaultPath }
    else failwithf "engine.json5 not found (checked %s, %s)" userPath defaultPath

/// The resolved config source (available after load).
let mutable Source : ConfigSource = { Path = ""; Label = ""; Version = 0 }

let private loadRendering () : RenderingConfig =
    let userPath = Path.Combine(PersistentDir, "configs", "heatmaps.json5")
    let defaultPath = Path.Combine(ModDir, "configs", "heatmaps.json5")

    let heatmapPath =
        if File.Exists(userPath) then userPath
        elif File.Exists(defaultPath) then defaultPath
        else ""

    if heatmapPath = "" then
        // Defaults
        { MinTilePixels = 64; Gamma = 0.35f; FontFamily = "DejaVu Sans Mono"
          ScoreFontScale = 0.32f; LabelFontScale = 0.33f
          ActorBorder = { Margin = 2; Thickness = 3; Color = [|255uy; 50uy; 50uy; 220uy|] }
          BestTileBorder = { Margin = 1; Thickness = 2; Color = [|50uy; 255uy; 50uy; 230uy|] }
          MoveDestBorder = { Margin = 3; Thickness = 2; Color = [|60uy; 140uy; 255uy; 230uy|] }
          VisionColor = [|255uy; 220uy; 50uy; 200uy|]
          FactionColors = Map.empty }
    else
        let json = stripComments (File.ReadAllText(heatmapPath))
        let doc = JsonDocument.Parse(json)
        let r = doc.RootElement
        let borders = r.GetProperty("borders")
        { MinTilePixels = r.GetProperty("minTilePixels").GetInt32()
          Gamma = r.GetProperty("gamma").GetSingle()
          FontFamily = r.GetProperty("fontFamily").GetString()
          ScoreFontScale = r.GetProperty("scoreFontScale").GetSingle()
          LabelFontScale = r.GetProperty("labelFontScale").GetSingle()
          ActorBorder = parseBorder (borders.GetProperty("actor"))
          BestTileBorder = parseBorder (borders.GetProperty("bestTile"))
          MoveDestBorder = parseBorder (borders.GetProperty("moveDest"))
          VisionColor = parseColorArray (borders.GetProperty("vision"))
          FactionColors = parseFactionColors (r.GetProperty("factionColors")) }

let private load () : TacticalEngineConfig =
    let source = resolveConfigPath ()
    Source <- source
    let configPath = source.Path

    let json = stripComments (File.ReadAllText(configPath))
    let doc = JsonDocument.Parse(json)
    let root = doc.RootElement

    { Port = root.GetProperty("port").GetInt32()
      BridgePort = match root.TryGetProperty("bridge_port") with | true, v -> v.GetInt32() | _ -> 7655
      CommandPort = match root.TryGetProperty("command_port") with | true, v -> v.GetInt32() | _ -> 7661
      Rendering = loadRendering () }

/// Singleton config — loaded once at module init.
let Current = load ()

// --- Behaviour presets (behaviour.json5) ---

/// Read a float from a JSON element. Fails if key is missing.
let readFloat (el: JsonElement) (key: string) =
    el.GetProperty(key).GetSingle()

/// Read an int from a JSON element. Fails if key is missing.
let readInt (el: JsonElement) (key: string) =
    el.GetProperty(key).GetInt32()

/// Pick a preset by name from a presets object. Fails if preset is missing.
let pickPreset (presets: JsonElement) (name: string) (parse: JsonElement -> 'a) =
    parse (presets.GetProperty(name))

/// Read the active preset name for a behaviour section.
let activePreset (root: JsonElement) (section: string) =
    match root.TryGetProperty("active") with
    | true, a -> match a.TryGetProperty(section) with | true, v -> v.GetString() | _ -> "default"
    | _ -> "default"

type BehaviourConfig = {
    Hooks: Map<string, string list>
    Root: JsonElement option
}

let mutable BehaviourSource : ConfigSource = { Path = ""; Label = ""; Version = 0 }

let private loadBehaviour () : BehaviourConfig =
    let userPath = Path.Combine(PersistentDir, "configs", "behaviour.json5")
    let defaultPath = Path.Combine(ModDir, "configs", "behaviour.json5")

    // Seed user config from mod default if missing
    if not (File.Exists(userPath)) && File.Exists(defaultPath) then
        try
            Directory.CreateDirectory(Path.GetDirectoryName(userPath)) |> ignore
            File.Copy(defaultPath, userPath)
        with _ -> ()

    let configPath =
        if File.Exists(userPath) then
            let userVer = readVersion userPath
            let defaultVer = if File.Exists(defaultPath) then readVersion defaultPath else 0
            if userVer >= defaultVer then
                BehaviourSource <- { Path = userPath; Label = "user"; Version = userVer }
                userPath
            else
                BehaviourSource <- { Path = defaultPath; Label = "default"; Version = defaultVer }
                defaultPath
        elif File.Exists(defaultPath) then
            BehaviourSource <- { Path = defaultPath; Label = "default"; Version = readVersion defaultPath }
            defaultPath
        else
            // No file — use hardcoded defaults
            BehaviourSource <- { Path = ""; Label = "builtin"; Version = 0 }
            ""

    let defaultHooks = Map.ofList [
        "OnTacticalReady", ["roaming-init"; "pack-init"]
        "OnTurnEnd", ["roaming-behaviour"; "reposition-behaviour"; "pack-behaviour"; "guard-vip-behaviour"]
    ]

    if configPath = "" then
        { Hooks = defaultHooks; Root = None }
    else
        let json = stripComments (File.ReadAllText(configPath))
        let doc = JsonDocument.Parse(json)
        let root = doc.RootElement

        let hooks =
            match root.TryGetProperty("hooks") with
            | true, hooksEl ->
                [ for prop in hooksEl.EnumerateObject() ->
                    prop.Name, [ for item in prop.Value.EnumerateArray() -> item.GetString() ] ]
                |> Map.ofList
            | _ -> defaultHooks

        { Hooks = hooks; Root = Some root }

/// Singleton behaviour config.
let Behaviour = loadBehaviour ()

// --- Game events (game_events.json5) ---

let mutable GameEventsSource : ConfigSource = { Path = ""; Label = ""; Version = 0 }

let private loadGameEvents () : Set<string> =
    let userPath = Path.Combine(PersistentDir, "configs", "game_events.json5")
    let defaultPath = Path.Combine(ModDir, "configs", "game_events.json5")

    if not (File.Exists(userPath)) && File.Exists(defaultPath) then
        try
            Directory.CreateDirectory(Path.GetDirectoryName(userPath)) |> ignore
            File.Copy(defaultPath, userPath)
        with _ -> ()

    let configPath =
        if File.Exists(userPath) then
            let userVer = readVersion userPath
            let defaultVer = if File.Exists(defaultPath) then readVersion defaultPath else 0
            if userVer >= defaultVer then
                GameEventsSource <- { Path = userPath; Label = "user"; Version = userVer }
                userPath
            else
                GameEventsSource <- { Path = defaultPath; Label = "default"; Version = defaultVer }
                defaultPath
        elif File.Exists(defaultPath) then
            GameEventsSource <- { Path = defaultPath; Label = "default"; Version = readVersion defaultPath }
            defaultPath
        else ""

    if configPath = "" then Set.empty
    else
        let json = stripComments (File.ReadAllText(configPath))
        let doc = JsonDocument.Parse(json)
        let root = doc.RootElement
        match root.TryGetProperty("active") with
        | true, arr -> [ for item in arr.EnumerateArray() -> item.GetString() ] |> Set.ofList
        | _ -> Set.empty

/// Active game events set.
let GameEvents = loadGameEvents ()
