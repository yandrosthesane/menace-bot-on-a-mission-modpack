/// Shape test node — assigns AI actors to BOAM letter positions based on round.
/// Temporary test node. Will be replaced by proper behavior nodes.
module BOAM.TacticalEngine.Nodes.ShapeTileModifier

open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.NodeContext
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Keys

let private shapes = [|
    [| (2,15);(2,17);(2,19);(2,21);(2,23);(2,25);(2,27)   // B
       (4,27);(6,27);(8,27);(10,27);(4,21);(6,21);(8,21);(10,21)
       (4,15);(6,15);(8,15);(10,15);(10,25);(10,23);(10,19);(10,17)
       (8,25);(8,23);(8,19);(8,17) |]
    [| (12,15);(12,17);(12,19);(12,21);(12,23);(12,25);(12,27)  // O
       (20,15);(20,17);(20,19);(20,21);(20,23);(20,25);(20,27)
       (14,27);(16,27);(18,27);(14,15);(16,15);(18,15)
       (14,25);(18,25);(14,17);(18,17);(16,23);(16,21);(16,19) |]
    [| (22,15);(22,17);(22,19);(22,21);(22,23);(22,25);(22,27)  // A
       (30,15);(30,17);(30,19);(30,21);(30,23);(30,25);(30,27)
       (24,27);(26,27);(28,27);(24,21);(26,21);(28,21)
       (24,25);(28,25);(24,23);(28,23);(24,19);(26,19);(28,19) |]
    [| (32,15);(32,17);(32,19);(32,21);(32,23);(32,25);(32,27)  // M
       (40,15);(40,17);(40,19);(40,21);(40,23);(40,25);(40,27)
       (34,27);(36,27);(38,27);(34,25);(36,23);(38,25);(36,21)
       (34,23);(38,23);(34,15);(36,15);(38,15);(36,19) |]
|]

let private shapeNames = [| "B"; "O"; "A"; "M" |]

let private shapeIndexForRound round =
    if round <= 15 then 0
    elif round <= 25 then 1
    elif round <= 35 then 2
    elif round <= 45 then 3
    else -1

/// The node definition. Register this in the graph.
let node : NodeDef = {
    Name = "shape-tile-modifier"
    Hook = OnTurnEnd
    Timing = Prefix
    Reads = [ "ai-actors" ]
    Writes = [ "tile-modifiers" ]
    Run = fun ctx ->
        let actors = ctx |> NodeContext.readOrDefault aiActors [||]
        if actors.Length = 0 then ()
        else
        let round = ctx.Faction.Round
        let idx = shapeIndexForRound round
        if idx < 0 || idx >= shapes.Length then
            ctx |> NodeContext.write tileModifiers Map.empty
        else
            let targets = shapes.[idx]
            let count = min actors.Length targets.Length
            let modifiers =
                [ for i in 0 .. count - 1 do
                    let (tx, tz) = targets.[i]
                    actors.[i], { TargetX = tx; TargetZ = tz; AddUtility = 20000f; SuppressAttack = true } ]
                |> Map.ofList
            ctx |> NodeContext.write tileModifiers modifiers
            ctx.Log (sprintf "shape %s → %d actors (round %d)" shapeNames.[idx] count round)
}
