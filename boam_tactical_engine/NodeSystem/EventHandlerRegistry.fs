/// Shared registry for node-owned event handlers.
/// Nodes call registerHandler at module init time. EventHandlers.fs reads from this
/// registry at startup and wraps each handler into the dispatch table.
/// Module init is triggered by banner cfg access in Program.fs, so handlers
/// are guaranteed to be registered before dispatch table construction.
module BOAM.TacticalEngine.EventHandlerRegistry

open System.Text.Json

type NodeEventHandler = StateStore.StateStore -> JsonElement -> unit

let private handlers = System.Collections.Generic.Dictionary<string, NodeEventHandler>()

let registerHandler (eventName: string) (handler: NodeEventHandler) =
    handlers.[eventName] <- handler

let getAll () : (string * NodeEventHandler) list =
    [ for kv in handlers -> kv.Key, kv.Value ]
