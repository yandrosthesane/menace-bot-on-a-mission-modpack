/// Icon setup — generates icons from extracted game sprites or built-in fallback.
/// Runs at engine startup when icons are missing or --icons-force is passed.
module BOAM.TacticalEngine.IconSetup

open System
open System.IO
open System.IO.Compression
open System.Text.Json
open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open SixLabors.ImageSharp.Processing
open BOAM.TacticalEngine.Config
open BOAM.TacticalEngine.Logging

type private Entry = {
    Dir: string
    Source: string
    Output: string
    Size: int
}

type private IconConfig = {
    DefaultSize: int
    OutputBase: string
    AssetsBase: string
    Sources: Map<string, string>
    Entries: Entry list
}

type IconResult = {
    Generated: int
    Skipped: int
    Missing: int
    Errors: int
    OutputDir: string
}

let private parseConfig (configPath: string) =
    let json = Config.stripComments (File.ReadAllText(configPath))
    let doc = JsonDocument.Parse(json)
    let root = doc.RootElement

    let defaults = root.GetProperty("defaults")
    let defaultSize = defaults.GetProperty("size").GetInt32()

    let userDataDir = Path.Combine(GameDir, "UserData")

    let outputBase =
        match defaults.TryGetProperty("output_base") with
        | true, v ->
            let path = v.GetString()
            if Path.IsPathRooted(path) then path
            else Path.Combine(userDataDir, path)
        | _ -> Path.Combine(PersistentDir, "icons")

    let assetsBase =
        match defaults.TryGetProperty("assets_base") with
        | true, v ->
            let path = v.GetString()
            if Path.IsPathRooted(path) then path
            else Path.Combine(userDataDir, path)
        | _ -> Path.Combine(userDataDir, "ExtractedData", "Assets")

    let sources =
        [ for prop in root.GetProperty("sources").EnumerateObject() ->
            let path = prop.Value.GetString()
            let resolved =
                if String.IsNullOrEmpty(path) then assetsBase
                elif Path.IsPathRooted(path) then path
                else Path.Combine(userDataDir, path)
            prop.Name, resolved ]
        |> Map.ofList

    let parseSection (name: string) =
        match root.TryGetProperty(name) with
        | true, arr ->
            [ for el in arr.EnumerateArray() ->
                let dir = match el.TryGetProperty("dir") with | true, v -> v.GetString() | _ -> ""
                let size = match el.TryGetProperty("size") with | true, v -> v.GetInt32() | _ -> defaultSize
                { Dir = dir; Source = el.GetProperty("source").GetString()
                  Output = el.GetProperty("output").GetString(); Size = size } ]
        | _ -> []

    { DefaultSize = defaultSize
      OutputBase = outputBase
      AssetsBase = assetsBase
      Sources = sources
      Entries = parseSection "factions" @ parseSection "templates" @ parseSection "leaders" }

let private resizeAndSave (srcPath: string) (outPath: string) (size: int) =
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)) |> ignore
    use img = Image.Load<Rgba32>(srcPath)
    if img.Width <> size || img.Height <> size then
        img.Mutate(fun ctx -> ctx.Resize(size, size, KnownResamplers.Lanczos3) |> ignore)
    img.Save(outPath)

/// Resolve the icon-config.json5 path: user → mod default.
let resolveConfigPath () =
    let userCfg = Path.Combine(PersistentDir, "configs", "icon-config.json5")
    let defaultCfg = Path.Combine(ModDir, "configs", "icon-config.json5")
    if File.Exists(userCfg) then Some userCfg
    elif File.Exists(defaultCfg) then Some defaultCfg
    else None

/// Check if extracted game assets exist at the configured path.
let private assetsExist (config: IconConfig) =
    Directory.Exists(config.AssetsBase)
    && Directory.GetFiles(config.AssetsBase, "*.png", SearchOption.AllDirectories).Length > 0

/// Extract the built-in fallback icon pack to the output directory.
let private extractFallback (outputBase: string) =
    let asm = Reflection.Assembly.GetExecutingAssembly()
    use stream = asm.GetManifestResourceStream("icons.zip")
    if stream = null then
        logWarn "Built-in icon pack not found in engine binary"
        0
    else
        use archive = new ZipArchive(stream, ZipArchiveMode.Read)
        let mutable count = 0
        for entry in archive.Entries do
            if entry.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) then
                // Strip leading "icons/" prefix from zip entries
                let relativePath =
                    if entry.FullName.StartsWith("icons/") then entry.FullName.Substring(6)
                    else entry.FullName
                let outPath = Path.Combine(outputBase, relativePath)
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)) |> ignore
                use entryStream = entry.Open()
                use outFile = File.Create(outPath)
                entryStream.CopyTo(outFile)
                count <- count + 1
        logInfo (sprintf "Extracted %d icons from built-in pack" count)
        count

/// Read a single line from stdin, with a default value if empty.
let private prompt (message: string) (defaultValue: string) =
    printf "%s " message
    Console.Out.Flush()
    let input = Console.ReadLine()
    if String.IsNullOrWhiteSpace(input) then defaultValue
    else input.Trim()

/// Run the icon pipeline from extracted assets. Returns results summary.
let private generate (config: IconConfig) (force: bool) =
    logInfo (sprintf "Icon pipeline: %d entries, output=%s" config.Entries.Length config.OutputBase)

    let mutable generated = 0
    let mutable skipped = 0
    let mutable missing = 0
    let mutable errors = 0

    for entry in config.Entries do
        let baseDir =
            match config.Sources.TryFind(entry.Dir) with
            | Some d -> d
            | None ->
                logWarn (sprintf "Unknown source dir '%s' for %s" entry.Dir entry.Output)
                errors <- errors + 1
                ""

        if baseDir <> "" then
            let srcPath = Path.Combine(baseDir, entry.Source)
            let outPath = Path.Combine(config.OutputBase, entry.Output)

            if not (File.Exists(srcPath)) then
                missing <- missing + 1
            elif File.Exists(outPath) && not force then
                skipped <- skipped + 1
            else
                try
                    resizeAndSave srcPath outPath entry.Size
                    generated <- generated + 1
                with ex ->
                    logWarn (sprintf "Icon fail: %s (%s)" entry.Output ex.Message)
                    errors <- errors + 1

    { Generated = generated; Skipped = skipped; Missing = missing; Errors = errors
      OutputDir = config.OutputBase }

/// Interactive icon setup — called when no icons exist.
/// Checks for assets, offers generation or download, handles user choice.
let interactiveSetup () =
    match resolveConfigPath () with
    | None ->
        logWarn "icon-config.json5 not found — skipping icon setup"
        { Generated = 0; Skipped = 0; Missing = 0; Errors = 0; OutputDir = "" }
    | Some configPath ->
        let config = parseConfig configPath
        printfn ""
        printfn "  Expected icons at: %s" (dim config.OutputBase)
        printfn "  %s" (yellow "No icons found — running icon setup.")
        printfn "  %s" (yellow "If the game is running, quit and restart it after this step.")
        printfn ""
        let hasAssets = assetsExist config
        printfn "  %s" (bold "Icon setup options:")
        if hasAssets then
            printfn "    [1] Generate from game assets at %s" (dim config.AssetsBase)
        else
            printfn "    [1] Generate from game assets %s — %s" (dim "(not found)") (dim config.AssetsBase)
        printfn "    [2] Use fallback icon pack"
        printfn "    [3] Skip this time — colored squares will be used instead of icons"
        printfn ""
        let defaultChoice = if hasAssets then "1" else "2"
        let choice = prompt (sprintf "  Choice [1/2/3] (default=%s):" defaultChoice) defaultChoice
        let empty = { Generated = 0; Skipped = 0; Missing = 0; Errors = 0; OutputDir = config.OutputBase }
        match choice with
        | "1" ->
            if not hasAssets then
                logWarn (sprintf "Game assets not found at %s" config.AssetsBase)
                logWarn "Extract game assets first (MelonLoader > ExtractedData) then restart the engine."
                empty
            else
                generate config true
        | "2" ->
            let count = extractFallback config.OutputBase
            { empty with Generated = count }
        | _ ->
            printfn "  Skipped — using text labels as fallback."
            empty
