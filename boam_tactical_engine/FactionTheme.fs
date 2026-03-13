/// Faction visual configuration — colors, icon names, label prefixes.
module BOAM.Sidecar.FactionTheme

open SixLabors.ImageSharp.PixelFormats

/// Faction colors matching TacticalMap conventions.
let factionColor (factionIdx: int) =
    match factionIdx with
    | 1 -> Rgba32(60uy, 140uy, 255uy, 200uy)   // Player — blue
    | 2 -> Rgba32(80uy, 160uy, 255uy, 200uy)   // PlayerAI — light blue
    | 3 -> Rgba32(200uy, 200uy, 100uy, 200uy)  // Civilian — yellow
    | 4 -> Rgba32(100uy, 200uy, 100uy, 200uy)  // AlliedLocalForces — green
    | 5 -> Rgba32(200uy, 100uy, 50uy, 200uy)   // EnemyLocalForces — orange
    | 6 -> Rgba32(180uy, 50uy, 180uy, 200uy)   // Pirates — purple
    | 7 -> Rgba32(255uy, 60uy, 60uy, 200uy)    // Wildlife — red
    | 8 -> Rgba32(160uy, 160uy, 160uy, 200uy)  // Constructs — gray
    | 9 -> Rgba32(200uy, 50uy, 50uy, 200uy)    // RogueArmy — dark red
    | _ -> Rgba32(128uy, 128uy, 128uy, 200uy)  // Unknown — gray

/// Faction index → faction icon filename.
let factionIconName (factionIdx: int) =
    match factionIdx with
    | 0 -> "neutral" | 1 -> "player" | 2 -> "playerai" | 3 -> "civilian"
    | 4 -> "allied" | 5 -> "enemy_local" | 6 -> "pirates" | 7 -> "wildlife"
    | 8 -> "constructs" | 9 -> "rogue_army" | _ -> "neutral"

/// Faction prefix for unit labels.
let factionPrefix (factionIdx: int) =
    match factionIdx with
    | 1 -> "P" | 2 -> "PA" | 3 -> "C" | 4 -> "A"
    | 5 -> "E" | 6 -> "Pi" | 7 -> "W" | 8 -> "Co" | 9 -> "R"
    | _ -> "?"
