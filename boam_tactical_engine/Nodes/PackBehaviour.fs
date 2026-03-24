/// PackBehaviour node — encourages AI units to form small packs.
/// Computes density-based pack scores: attraction toward a few allies, repulsion from large clumps.
/// Allies that already acted this round (anchored) have stronger influence.
/// Runs after RoamingBehaviour, merges pack scores into existing tile modifier maps.
module BOAM.TacticalEngine.Nodes.PackBehaviour

open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.NodeContext
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Keys

// Pack scoring parameters
let private radius = 7f            // Influence range per ally (tiles)
let private peak = 2.5f            // Ideal density — sweet spot
let private attraction = 80f       // Utility per density unit below peak
let private crowdPenalty = 120f    // Penalty per density unit above peak
let private anchoredWeight = 1.0f  // Influence multiplier for allies that already acted
let private unactedWeight = 0.3f   // Influence multiplier for allies that haven't acted
let private contactBonus = 0.5f    // Extra influence for allies that can see an opponent

/// Compute pack score for a single tile given all ally positions.
/// Each ally is (position, hasActed, inContact).
let private packScoreAtTile (tileX: int) (tileZ: int) (allies: (TilePos * bool * bool) list) =
    let mutable density = 0f
    for (pos, hasActed, inContact) in allies do
        let dx = float32 (tileX - pos.X)
        let dz = float32 (tileZ - pos.Z)
        let dist = sqrt (dx * dx + dz * dz)
        if dist < radius then
            let influence = (1f - dist / radius)
            let weight = (if hasActed then anchoredWeight else unactedWeight) + (if inContact then contactBonus else 0f)
            density <- density + influence * weight
    // Crowd curve: rises to peak, then drops
    attraction * min density peak - crowdPenalty * max 0f (density - peak)

/// The node definition. Register this in the graph after RoamingBehaviour.
let node : NodeDef = {
    Name = "pack-behaviour"
    Hook = OnTurnEnd
    Timing = Prefix
    Reads = [ "tile-modifiers"; "turn-end-actor"; "actor-positions" ]
    Writes = [ "tile-modifiers" ]
    Run = fun ctx ->
        let existing = ctx |> NodeContext.readOrDefault tileModifiers Map.empty
        let positions = ctx |> NodeContext.readOrDefault actorPositions Map.empty
        let actorOpt = ctx |> NodeContext.read turnEndActor

        match actorOpt with
        | None -> ()
        | Some a ->
            // Build ally list: all other actors' positions + acted state + contact state
            let allies =
                positions
                |> Map.toList
                |> List.choose (fun (id, state) ->
                    if id <> a.Actor then Some (state.Position, state.HasActed, state.InContact)
                    else None)

            if allies.IsEmpty then
                ctx.Log (sprintf "%s: no allies for pack scoring" a.Actor)
            else
                // Get existing tile map for this actor (from roaming or empty)
                let actorTiles = existing |> Map.tryFind a.Actor |> Option.defaultValue Map.empty

                if Map.isEmpty actorTiles then
                    ctx.Log (sprintf "%s: no tiles to score (no roaming map)" a.Actor)
                else
                    // Add pack scores to each tile in the actor's existing map
                    let mutable minScore = System.Single.MaxValue
                    let mutable maxScore = System.Single.MinValue
                    let updatedTiles =
                        actorTiles |> Map.map (fun pos existingUtility ->
                            let ps = packScoreAtTile pos.X pos.Z allies
                            if ps < minScore then minScore <- ps
                            if ps > maxScore then maxScore <- ps
                            existingUtility + ps)

                    let acted = allies |> List.filter (fun (_, a, _) -> a) |> List.length
                    let contact = allies |> List.filter (fun (_, _, c) -> c) |> List.length
                    ctx.Log (sprintf "%s: pack scored %d tiles, %d allies (%d acted, %d contact), score range %.0f..%.0f"
                        a.Actor (Map.count updatedTiles) (List.length allies) acted contact minScore maxScore)

                    ctx |> NodeContext.write tileModifiers (existing |> Map.add a.Actor updatedTiles)
}
