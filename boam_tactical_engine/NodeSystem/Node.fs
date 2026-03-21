/// Node definition — the unit of work in BOAM.
/// Each node declares its hook, timing, state dependencies, and a run function.
module BOAM.TacticalEngine.Node

open BOAM.TacticalEngine.NodeContext

/// When a node fires relative to the hooked method.
type Timing =
    | Prefix
    | Postfix

/// Which game hook point a node binds to.
type HookPoint =
    | OnTurnStart
    | OnRoundStart
    | ConsiderZones
    | SetTile
    | OnDamageReceived

/// A registered node.
type NodeDef = {
    Name: string
    Hook: HookPoint
    Timing: Timing
    Reads: string list
    Writes: string list
    Run: NodeContext -> unit
}
