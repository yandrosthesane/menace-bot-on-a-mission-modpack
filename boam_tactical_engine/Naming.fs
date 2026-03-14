/// Label and filename logic for units and heatmaps.
module BOAM.TacticalEngine.Naming

open BOAM.TacticalEngine.GameTypes

/// Full template name after dot prefix (e.g., "enemy.alien_stinger" → "alien_stinger").
/// Used for icon file lookup — no stopword stripping.
let templateFileName (name: string) =
    if System.String.IsNullOrEmpty(name) then ""
    else
        match name.LastIndexOf('.') with
        | -1 -> name
        | i -> name.Substring(i + 1)

/// Build unit label from stable UUID.
/// "player.carda" → "player.carda", "wildlife.alien_stinger.1" → "wildlife.alien_stinger.1"
/// The UUID is already human-readable and unique.
let unitLabel (u: UnitInfo) = u.Actor

/// Build heatmap filename label from stable UUID and round.
/// "player.carda" → "player_carda_r01"
let makeHeatmapLabel (actor: string) (round: int) =
    sprintf "%s_r%02d" (actor.Replace(".", "_")) round
