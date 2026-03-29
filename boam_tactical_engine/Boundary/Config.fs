/// Loads engine.json5 at startup. No hot reload.
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
    ActionLogging: bool
    AiLogging: bool
    CriterionLogging: bool
    Rendering: RenderingConfig
}

// --- Behaviour config (separate file: behaviour.json5) ---

type RoamingPreset = {
    BaseUtility: float32
    UtilityFraction: float32
    EngagementRadius: float32
}
type RepositionPreset = {
    MaxUtilityByAttacks: float32
    UtilityByAttacksFraction: float32
    ApproachBias: float32
}
type GuardVipPreset = {
    Radius: float32
    BaseSafety: float32
    SafetyFraction: float32
    Weight: float32
}
type PackPreset = {
    Radius: float32
    Peak: float32
    BaseSafety: float32
    SafetyFraction: float32
    CrowdPenalty: float32
    AnchoredWeight: float32
    UnactedWeight: float32
    ContactBonus: float32
    InitMultiplier: float32
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

/// Config source info for logging.
type ConfigSource = { Path: string; Label: string; Version: int }

/// Resolve config path: user persistent config → mod default → source dev fallback.
/// Returns the resolved path and source metadata.
/// Seeds the user config from mod default if it doesn't exist yet.
let private resolveConfigPath () =
    let exeDir = AppContext.BaseDirectory
    let gameDir =
        Environment.GetEnvironmentVariable("MENACE_GAME_DIR")
        |> Option.ofObj
        |> Option.defaultValue (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".steam/steam/steamapps/common/Menace"))
    let persistentDir = Path.Combine(gameDir, "UserData", "BOAM")
    let userPath = Path.Combine(persistentDir, "configs", "engine.json5")
    let modDir = Path.GetDirectoryName(exeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
    let defaultPath = Path.Combine(modDir, "configs", "engine.json5")

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

let private load () : TacticalEngineConfig =
    let source = resolveConfigPath ()
    Source <- source
    let configPath = source.Path

    let json = stripComments (File.ReadAllText(configPath))
    let doc = JsonDocument.Parse(json)
    let root = doc.RootElement
    let r = root.GetProperty("rendering")
    let borders = r.GetProperty("borders")

    { Port = root.GetProperty("port").GetInt32()
      BridgePort = match root.TryGetProperty("bridge_port") with | true, v -> v.GetInt32() | _ -> 7655
      CommandPort = match root.TryGetProperty("command_port") with | true, v -> v.GetInt32() | _ -> 7661
      Heatmaps = match root.TryGetProperty("heatmaps") with | true, v -> v.GetBoolean() | _ -> false
      ActionLogging = match root.TryGetProperty("action_logging") with | true, v -> v.GetBoolean() | _ -> false
      AiLogging = match root.TryGetProperty("ai_logging") with | true, v -> v.GetBoolean() | _ -> false
      CriterionLogging = match root.TryGetProperty("criterion_logging") with | true, v -> v.GetBoolean() | _ -> false
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

// --- Behaviour presets (behaviour.json5) ---

let private defaultRoaming : RoamingPreset = {
    BaseUtility = 100f; UtilityFraction = 1.0f; EngagementRadius = 20f
}
let private defaultReposition : RepositionPreset = {
    MaxUtilityByAttacks = 600f; UtilityByAttacksFraction = 2.0f; ApproachBias = 0.5f
}
let private defaultGuardVip : GuardVipPreset = {
    Radius = 15f; BaseSafety = 400f; SafetyFraction = 1.5f; Weight = 2.0f
}
let private defaultPack : PackPreset = {
    Radius = 20f; Peak = 4.0f; BaseSafety = 560f; SafetyFraction = 1.2f
    CrowdPenalty = 120f; AnchoredWeight = 1.0f; UnactedWeight = 0.3f
    ContactBonus = 1.5f; InitMultiplier = 3.0f
}

let private tryFloat (el: JsonElement) (key: string) (fallback: float32) =
    match el.TryGetProperty(key) with | true, v -> v.GetSingle() | _ -> fallback

let private parseRoaming (el: JsonElement) (defaults: RoamingPreset) : RoamingPreset = {
    BaseUtility = tryFloat el "baseUtility" defaults.BaseUtility
    UtilityFraction = tryFloat el "utilityFraction" defaults.UtilityFraction
    EngagementRadius = tryFloat el "engagementRadius" defaults.EngagementRadius
}

let private parseGuardVip (el: JsonElement) (defaults: GuardVipPreset) : GuardVipPreset = {
    Radius = tryFloat el "radius" defaults.Radius
    BaseSafety = tryFloat el "baseSafety" defaults.BaseSafety
    SafetyFraction = tryFloat el "safetyFraction" defaults.SafetyFraction
    Weight = tryFloat el "weight" defaults.Weight
}

let private parseReposition (el: JsonElement) (defaults: RepositionPreset) : RepositionPreset = {
    MaxUtilityByAttacks = tryFloat el "maxUtilityByAttacks" defaults.MaxUtilityByAttacks
    UtilityByAttacksFraction = tryFloat el "utilityByAttacksFraction" defaults.UtilityByAttacksFraction
    ApproachBias = tryFloat el "approachBias" defaults.ApproachBias
}

let private parsePack (el: JsonElement) (defaults: PackPreset) : PackPreset = {
    Radius = tryFloat el "radius" defaults.Radius
    Peak = tryFloat el "peak" defaults.Peak
    BaseSafety = tryFloat el "baseSafety" defaults.BaseSafety
    SafetyFraction = tryFloat el "safetyFraction" defaults.SafetyFraction
    CrowdPenalty = tryFloat el "crowdPenalty" defaults.CrowdPenalty
    AnchoredWeight = tryFloat el "anchoredWeight" defaults.AnchoredWeight
    UnactedWeight = tryFloat el "unactedWeight" defaults.UnactedWeight
    ContactBonus = tryFloat el "contactBonus" defaults.ContactBonus
    InitMultiplier = tryFloat el "initMultiplier" defaults.InitMultiplier
}

/// Pick a preset by name from a presets object, falling back to defaults.
let private pickPreset (presets: JsonElement) (name: string) (parse: JsonElement -> 'a) (defaults: 'a) =
    match presets.TryGetProperty(name) with
    | true, el -> parse el
    | _ -> defaults

type BehaviourConfig = {
    DataEvents: Set<string>          // active C# data events
    Hooks: Map<string, string list>  // hook point name → ordered list of node names
    Roaming: RoamingPreset
    Reposition: RepositionPreset
    Pack: PackPreset
    GuardVip: GuardVipPreset
}

let mutable BehaviourSource : ConfigSource = { Path = ""; Label = ""; Version = 0 }

let private loadBehaviour () : BehaviourConfig =
    let exeDir = AppDomain.CurrentDomain.BaseDirectory
    let gameDir =
        Environment.GetEnvironmentVariable("MENACE_GAME_DIR")
        |> Option.ofObj
        |> Option.defaultValue (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".steam/steam/steamapps/common/Menace"))
    let persistentDir = Path.Combine(gameDir, "UserData", "BOAM")
    let userPath = Path.Combine(persistentDir, "configs", "behaviour.json5")
    let modDir = Path.GetDirectoryName(exeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
    let defaultPath = Path.Combine(modDir, "configs", "behaviour.json5")

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

    let defaultDataEvents = Set.empty

    if configPath = "" then
        { DataEvents = defaultDataEvents; Hooks = defaultHooks; Roaming = defaultRoaming; Reposition = defaultReposition; Pack = defaultPack; GuardVip = defaultGuardVip }
    else
        let json = stripComments (File.ReadAllText(configPath))
        let doc = JsonDocument.Parse(json)
        let root = doc.RootElement

        // Read data events
        let dataEvents =
            match root.TryGetProperty("dataEvents") with
            | true, arr -> [ for item in arr.EnumerateArray() -> item.GetString() ] |> Set.ofList
            | _ -> defaultDataEvents

        // Read active preset names
        let activeRoaming = match root.TryGetProperty("active") with | true, a -> match a.TryGetProperty("roaming") with | true, v -> v.GetString() | _ -> "default" | _ -> "default"
        let activeReposition = match root.TryGetProperty("active") with | true, a -> match a.TryGetProperty("reposition") with | true, v -> v.GetString() | _ -> "default" | _ -> "default"
        let activePack = match root.TryGetProperty("active") with | true, a -> match a.TryGetProperty("pack") with | true, v -> v.GetString() | _ -> "default" | _ -> "default"
        let activeGuardVip = match root.TryGetProperty("active") with | true, a -> match a.TryGetProperty("guard-vip") with | true, v -> v.GetString() | _ -> "default" | _ -> "default"

        // Parse hook chains
        let hooks =
            match root.TryGetProperty("hooks") with
            | true, hooksEl ->
                [ for prop in hooksEl.EnumerateObject() ->
                    prop.Name, [ for item in prop.Value.EnumerateArray() -> item.GetString() ] ]
                |> Map.ofList
            | _ -> defaultHooks

        let roaming =
            match root.TryGetProperty("roaming") with
            | true, presets -> pickPreset presets activeRoaming (fun el -> parseRoaming el defaultRoaming) defaultRoaming
            | _ -> defaultRoaming
        let reposition =
            match root.TryGetProperty("reposition") with
            | true, presets -> pickPreset presets activeReposition (fun el -> parseReposition el defaultReposition) defaultReposition
            | _ -> defaultReposition
        let pack =
            match root.TryGetProperty("pack") with
            | true, presets -> pickPreset presets activePack (fun el -> parsePack el defaultPack) defaultPack
            | _ -> defaultPack

        let guard =
            match root.TryGetProperty("guard-vip") with
            | true, presets -> pickPreset presets activeGuardVip (fun el -> parseGuardVip el defaultGuardVip) defaultGuardVip
            | _ -> defaultGuardVip

        { DataEvents = dataEvents; Hooks = hooks; Roaming = roaming; Reposition = reposition; Pack = pack; GuardVip = guard }

/// Singleton behaviour config.
let Behaviour = loadBehaviour ()
