/// BOAM Icon Generator — reads icon-config.json5 and resizes source PNGs to output icons.
/// Cross-platform: works on both Linux (native) and Windows.
/// Usage: boam-icons [--force] [--config path/to/icon-config.json5]
module BOAM.AssetPipeline.Main

open System
open System.IO
open System.Text.Json
open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open SixLabors.ImageSharp.Processing

let private version = "1.0.0"

type Entry = {
    Dir: string
    Source: string
    Output: string
    Size: int
}

type Config = {
    DefaultSize: int
    OutputBase: string
    Sources: Map<string, string>
    Entries: Entry list
}

/// Strip // and /* */ comments from JSON5 so System.Text.Json can parse it.
let private stripComments (input: string) =
    let sb = Text.StringBuilder(input.Length)
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

/// Derive game directory paths from the executable location.
/// The binary lives at Mods/BOAM/boam-icons, so game dir is two levels up.
let private resolveGamePaths () =
    let exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
    // exeDir = .../Menace/Mods/BOAM  (or wherever the binary is)
    let boamModDir = exeDir
    let gameDir = Path.GetDirectoryName(Path.GetDirectoryName(boamModDir))
    let persistentDir =
        Environment.GetEnvironmentVariable("BOAM_PERSISTENT_ASSETS")
        |> Option.ofObj
        |> Option.defaultValue (Path.Combine(gameDir, "UserData", "BOAM"))
    boamModDir, persistentDir

let private parseConfig (configPath: string) =
    let json = stripComments (File.ReadAllText(configPath))
    let doc = JsonDocument.Parse(json)
    let root = doc.RootElement

    let defaults = root.GetProperty("defaults")
    let defaultSize = defaults.GetProperty("size").GetInt32()

    let boamModDir, persistentDir = resolveGamePaths ()
    let userDataDir = Path.GetDirectoryName(persistentDir)

    // output_base: use config value if absolute, otherwise relative to UserData/
    // This ensures generated icons survive mod deploys (Mods/BOAM/ gets wiped)
    let outputBase =
        match defaults.TryGetProperty("output_base") with
        | true, v ->
            let path = v.GetString()
            if Path.IsPathRooted(path) then path
            else Path.Combine(userDataDir, path)
        | _ -> Path.Combine(persistentDir, "icons")

    // assets_base: where extracted game assets live (relative to UserData/)
    let assetsBase =
        match defaults.TryGetProperty("assets_base") with
        | true, v ->
            let path = v.GetString()
            if Path.IsPathRooted(path) then path
            else Path.Combine(userDataDir, path)
        | _ -> Path.Combine(userDataDir, "ExtractedData", "Assets")

    // sources: empty values resolve to assets_base, relative paths to UserData/
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
      Sources = sources
      Entries = parseSection "factions" @ parseSection "templates" @ parseSection "leaders" }

let private resizeAndSave (srcPath: string) (outPath: string) (size: int) =
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)) |> ignore
    use img = Image.Load<Rgba32>(srcPath)
    if img.Width <> size || img.Height <> size then
        img.Mutate(fun ctx -> ctx.Resize(size, size, KnownResamplers.Lanczos3) |> ignore)
    img.Save(outPath)

[<EntryPoint>]
let main argv =
    let mutable force = false
    let mutable configPath = ""

    // Parse args
    let mutable i = 0
    while i < argv.Length do
        match argv.[i] with
        | "--force" -> force <- true
        | "--config" when i + 1 < argv.Length ->
            configPath <- argv.[i + 1]
            i <- i + 1
        | arg when arg.EndsWith(".json") || arg.EndsWith(".json5") -> configPath <- arg
        | "--help" | "-h" ->
            printfn "BOAM Icon Generator v%s" version
            printfn "Usage: boam-icons [--force] [--config icon-config.json5]"
            printfn ""
            printfn "Options:"
            printfn "  --force     Overwrite existing output files"
            printfn "  --config    Path to icon-config.json5 (default: ./icon-config.json5)"
            exit 0
        | other ->
            eprintfn "Unknown argument: %s" other
            exit 1
        i <- i + 1

    // Default config: same directory as executable
    if configPath = "" then
        let exeDir = AppContext.BaseDirectory
        let candidate = Path.Combine(exeDir, "icon-config.json5")
        if File.Exists(candidate) then configPath <- candidate
        else
            let cwdCandidate = Path.Combine(Environment.CurrentDirectory, "icon-config.json5")
            if File.Exists(cwdCandidate) then configPath <- cwdCandidate
            else
                eprintfn "ERROR: icon-config.json5 not found. Use --config to specify path."
                exit 1

    if not (File.Exists(configPath)) then
        eprintfn "ERROR: Config not found: %s" configPath
        exit 1

    printfn "BOAM Icon Generator v%s" version
    printfn "  Config: %s" configPath

    let config = parseConfig configPath

    printfn "  Output: %s" config.OutputBase
    printfn "  Default size: %dx%d" config.DefaultSize config.DefaultSize
    printfn "  Entries: %d" config.Entries.Length
    printfn ""

    let mutable generated = 0
    let mutable skipped = 0
    let mutable missing = 0
    let mutable errors = 0

    for entry in config.Entries do
        let baseDir =
            match config.Sources.TryFind(entry.Dir) with
            | Some d -> d
            | None ->
                eprintfn "  WARN: unknown source dir '%s' for %s" entry.Dir entry.Output
                errors <- errors + 1
                ""

        if baseDir <> "" then
            let srcPath = Path.Combine(baseDir, entry.Source)
            let outPath = Path.Combine(config.OutputBase, entry.Output)

            if not (File.Exists(srcPath)) then
                printfn "  MISSING: %s" entry.Source
                missing <- missing + 1
            elif File.Exists(outPath) && not force then
                skipped <- skipped + 1
            else
                try
                    resizeAndSave srcPath outPath entry.Size
                    printfn "  OK: %s  (%dx%d)" entry.Output entry.Size entry.Size
                    generated <- generated + 1
                with ex ->
                    printfn "  FAIL: %s (%s)" entry.Output ex.Message
                    errors <- errors + 1

    printfn ""
    printfn "Done: %d generated, %d skipped, %d missing, %d errors" generated skipped missing errors
    if errors > 0 then 1 else 0
