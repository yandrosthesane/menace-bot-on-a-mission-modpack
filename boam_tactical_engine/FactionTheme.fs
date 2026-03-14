/// Faction visual configuration — colors, icon names, label prefixes.
module BOAM.TacticalEngine.FactionTheme

open SixLabors.ImageSharp.PixelFormats
open BOAM.TacticalEngine.Config

let private cfg = Current.Rendering

let private toRgba (arr: byte array) =
    Rgba32(arr.[0], arr.[1], arr.[2], arr.[3])

let private defaultColor = toRgba (cfg.FactionColors |> Map.find 0)

/// Faction colors from config, with fallback to faction 0.
let factionColor (factionIdx: int) =
    match cfg.FactionColors |> Map.tryFind factionIdx with
    | Some c -> toRgba c
    | None -> defaultColor

/// Faction index → faction icon filename.
let factionIconName (factionIdx: int) =
    match factionIdx with
    | 0 -> "neutral" | 1 -> "player" | 2 -> "playerai" | 3 -> "civilian"
    | 4 -> "allied" | 5 -> "enemy_local" | 6 -> "pirates" | 7 -> "wildlife"
    | 8 -> "constructs" | 9 -> "rogue_army" | _ -> "neutral"

