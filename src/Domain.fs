/// BOAM domain model — core types for the node registration framework.
/// This file compiles first; the plugin and all other modules reference these types.
module BOAM.Domain

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

/// A registered node — the unit of work in BOAM.
/// Each node declares what state it reads and writes,
/// enabling the framework to validate dependencies at registration time.
type NodeDef = {
    Name: string
    Hook: HookPoint
    Timing: Timing
    Reads: string list
    Writes: string list
}
