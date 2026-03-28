/// Node catalogue — collects all available nodes by name.
/// Config-driven registration: the behaviour config declares which nodes run on which hooks.
module BOAM.TacticalEngine.Catalogue

open BOAM.TacticalEngine.Node

/// All known nodes, keyed by name. Populated at startup by each node module.
let private catalogue = System.Collections.Generic.Dictionary<string, NodeDef>()

/// Register a node in the catalogue (called by node modules at init).
let register (node: NodeDef) =
    catalogue.[node.Name] <- node

/// Look up a node by name.
let tryFind (name: string) : NodeDef option =
    match catalogue.TryGetValue(name) with
    | true, n -> Some n
    | _ -> None

/// All registered node names.
let allNames () = catalogue.Keys |> Seq.toList

/// Parse a hook point string from config to the HookPoint DU.
let parseHookPoint (s: string) =
    match s.ToLowerInvariant() with
    | "onturnstart" | "on-turn-start" -> Some OnTurnStart
    | "onturnend" | "on-turn-end" -> Some OnTurnEnd
    | "ontacticalready" | "on-tactical-ready" -> Some OnTacticalReady
    | "onroundstart" | "on-round-start" -> Some OnRoundStart
    | _ -> None
