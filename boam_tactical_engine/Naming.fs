/// Label and filename logic for units and heatmaps.
module BOAM.TacticalEngine.Naming

open BOAM.TacticalEngine.GameTypes

/// Noise words to strip from template names for compact labels.
let private stopwords =
    Set.ofList [ "alien"; "soldier"; "civilian"; "enemy"; "small"; "big"; "light"; "heavy"; "comp"; "01"; "02"; "03"; "04"; "05" ]

/// Short name from template name: strip prefix, drop stopwords, keep last 2 segments.
let shortName (name: string) =
    if System.String.IsNullOrEmpty(name) then ""
    else
        let afterDot = match name.LastIndexOf('.') with | -1 -> name | i -> name.Substring(i + 1)
        let segments = afterDot.Split('_') |> Array.filter (fun s -> not (stopwords.Contains(s)))
        if segments.Length = 0 then
            let parts = afterDot.Split('_')
            parts.[parts.Length - 1]
        elif segments.Length <= 2 then
            System.String.Join("_", segments)
        else
            System.String.Join("_", segments.[segments.Length - 2 ..])

/// Full template name after dot prefix (e.g., "enemy.alien_stinger" → "alien_stinger").
/// Used for icon file lookup — no stopword stripping.
let templateFileName (name: string) =
    if System.String.IsNullOrEmpty(name) then ""
    else
        match name.LastIndexOf('.') with
        | -1 -> name
        | i -> name.Substring(i + 1)

/// Display name for a unit: prefer leader nickname, fallback to template shortName.
let unitDisplayName (u: UnitInfo) =
    if not (System.String.IsNullOrEmpty(u.Leader)) then u.Leader.ToLower()
    else shortName u.Name

/// Build unit labels: assign per-faction index, produce "W_stinger_1" or "P_rewa_1" style labels.
let buildUnitLabels (units: UnitInfo list) =
    let counters = System.Collections.Generic.Dictionary<int, int>()
    units |> List.map (fun u ->
        let idx =
            match counters.TryGetValue(u.Faction) with
            | true, n -> counters.[u.Faction] <- n + 1; n + 1
            | _ -> counters.[u.Faction] <- 1; 1
        let prefix = FactionTheme.factionPrefix u.Faction
        let dn = unitDisplayName u
        let label = if dn = "" then sprintf "%s_%d" prefix idx else sprintf "%s_%s_%d" prefix dn idx
        u, label)

/// Build heatmap filename label from stable UUID and round.
/// "player.carda" → "player_carda_r01"
let makeHeatmapLabel (actor: string) (round: int) =
    sprintf "%s_r%02d" (actor.Replace(".", "_")) round
