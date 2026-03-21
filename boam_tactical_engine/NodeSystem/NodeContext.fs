/// Context passed to each node's run function.
/// Provides typed read/write access to the state store and hook-specific data.
module BOAM.TacticalEngine.NodeContext

open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.StateKey
open BOAM.TacticalEngine.StateStore

/// The context a node receives when it runs.
type NodeContext = {
    /// The faction state for this hook invocation.
    Faction: FactionState
    /// Access to the shared state store.
    Store: StateStore
    /// Name of the node currently executing (for logging).
    NodeName: string
    /// Log function (writes to tactical engine console).
    Log: string -> unit
}

module NodeContext =

    /// Read a typed value from the state store.
    let read<'t> (key: StateKey<'t>) (ctx: NodeContext) : 't option =
        ctx.Store.Read(key)

    /// Read with a default fallback.
    let readOrDefault<'t> (key: StateKey<'t>) (defaultValue: 't) (ctx: NodeContext) : 't =
        ctx.Store.ReadOrDefault(key, defaultValue)

    /// Write a typed value to the state store.
    let write<'t> (key: StateKey<'t>) (value: 't) (ctx: NodeContext) =
        ctx.Store.Write(key, value)

    /// Update a value using a transform function.
    let update<'t> (key: StateKey<'t>) (defaultValue: 't) (f: 't -> 't) (ctx: NodeContext) =
        ctx.Store.Update(key, defaultValue, f)
