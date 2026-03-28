/// Walker — executes registered nodes for a given hook invocation.
module BOAM.TacticalEngine.Walker

open System.Diagnostics
open BOAM.TacticalEngine.GameTypes
open BOAM.TacticalEngine.StateStore
open BOAM.TacticalEngine.NodeContext
open BOAM.TacticalEngine.Node
open BOAM.TacticalEngine.Registry

/// Result of running all nodes for a hook.
type WalkResult = {
    Hook: HookPoint
    Timing: Timing
    NodesRun: int
    NodesSkipped: int
    ElapsedMs: float
    Log: string list
}

/// Run all nodes registered for a specific hook+timing.
let run
    (registry: Registry)
    (store: StateStore)
    (hook: HookPoint)
    (timing: Timing)
    (faction: FactionState)
    (logFn: string -> unit)
    : WalkResult =

    let sw = Stopwatch.StartNew()
    let log = ResizeArray<string>()
    let mutable ran = 0
    let mutable skipped = 0

    let hookKey = hook, timing
    let nodes =
        match registry.ByHook().TryFind(hookKey) with
        | Some ns -> ns
        | None -> []

    let hookLabel = sprintf "%A.%A" hook timing
    log.Add(sprintf "--- %s --- Faction %d ---" hookLabel faction.FactionIndex)

    for node in nodes do
        let startMsg = sprintf "  >> %s" node.Name
        log.Add(startMsg)
        logFn startMsg
        let nodeSw = Stopwatch.StartNew()
        try
            let ctx = {
                Faction = faction
                Store = store
                NodeName = node.Name
                Log = fun msg ->
                    let entry = sprintf "  [%s] %s" node.Name msg
                    log.Add(entry)
                    logFn entry
            }
            node.Run ctx
            nodeSw.Stop()
            ran <- ran + 1
            let endMsg = sprintf "  << %s (%.2fms)" node.Name nodeSw.Elapsed.TotalMilliseconds
            log.Add(endMsg)
            logFn endMsg
        with ex ->
            nodeSw.Stop()
            skipped <- skipped + 1
            let failMsg = sprintf "  << %s FAILED: %s (%.2fms)" node.Name ex.Message nodeSw.Elapsed.TotalMilliseconds
            log.Add(failMsg)
            logFn failMsg

    sw.Stop()
    log.Add(sprintf "--- %s complete (%d ran, %d skipped, %.2fms) ---"
        hookLabel ran skipped sw.Elapsed.TotalMilliseconds)

    { Hook = hook
      Timing = timing
      NodesRun = ran
      NodesSkipped = skipped
      ElapsedMs = sw.Elapsed.TotalMilliseconds
      Log = log |> Seq.toList }
