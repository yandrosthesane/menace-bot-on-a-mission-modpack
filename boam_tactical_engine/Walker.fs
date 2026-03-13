/// Walker — executes registered nodes for a given hook invocation.
module BOAM.Sidecar.Walker

open System.Diagnostics
open BOAM.Sidecar.GameTypes
open BOAM.Sidecar.StateStore
open BOAM.Sidecar.NodeContext
open BOAM.Sidecar.Node
open BOAM.Sidecar.Registry

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
            log.Add(sprintf "  -> %s (%.2fms)" node.Name nodeSw.Elapsed.TotalMilliseconds)
        with ex ->
            nodeSw.Stop()
            skipped <- skipped + 1
            log.Add(sprintf "  -> %s FAILED: %s (%.2fms)" node.Name ex.Message nodeSw.Elapsed.TotalMilliseconds)
            logFn (sprintf "  [%s] ERROR: %s" node.Name ex.Message)

    sw.Stop()
    log.Add(sprintf "--- %s complete (%d ran, %d skipped, %.2fms) ---"
        hookLabel ran skipped sw.Elapsed.TotalMilliseconds)

    { Hook = hook
      Timing = timing
      NodesRun = ran
      NodesSkipped = skipped
      ElapsedMs = sw.Elapsed.TotalMilliseconds
      Log = log |> Seq.toList }
