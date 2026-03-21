/// Dual file + console logging with ANSI colors.
module BOAM.TacticalEngine.Logging

open System
open System.IO

let private logDir =
    let gameDir =
        Environment.GetEnvironmentVariable("MENACE_GAME_DIR")
        |> Option.ofObj
        |> Option.defaultValue (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".steam/steam/steamapps/common/Menace"))
    Path.Combine(gameDir, "Mods", "BOAM", "logs")

let private logFilePath =
    Directory.CreateDirectory(logDir) |> ignore
    Path.Combine(logDir, sprintf "tactical_engine_%s.log" (DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")))

let private logFile = new StreamWriter(logFilePath, true, Text.Encoding.UTF8, AutoFlush = true)

// ANSI color helpers
let private esc code s = sprintf "\x1b[%sm%s\x1b[0m" code s
let green  = esc "32"
let yellow = esc "33"
let cyan   = esc "36"
let red    = esc "31"
let dim    = esc "90"
let bold   = esc "1"

let private timestamp () = DateTime.Now.ToString("HH:mm:ss.fff")

let private log color tag msg =
    let ts = timestamp ()
    printfn "%s %s %s" (dim ts) (color (sprintf "[%s]" tag)) msg
    logFile.WriteLine(sprintf "%s [%s] %s" ts tag msg)

let logInfo   = log green "BOAM"
let logHook   = log yellow "HOOK"
let logWarn   = log red "WARN"
let logEngine = log cyan "ENGI"
