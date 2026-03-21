/// Node registry — collects nodes, groups by hook, validates dependencies.
module BOAM.TacticalEngine.Registry

open System.Collections.Generic
open BOAM.TacticalEngine.Node

/// Validation result for a single state key.
type KeyValidation =
    | Ok of key: string * writers: string list * readers: string list
    | OrphanedReader of key: string * readers: string list
    | WriteConflict of key: string * writers: string list

/// Full validation report.
type ValidationReport = {
    TotalNodes: int
    TotalHooks: int
    Keys: KeyValidation list
    HasWarnings: bool
}

type Registry() =
    let nodes = ResizeArray<NodeDef>()

    /// Register a list of nodes (from one mod).
    member _.Register(newNodes: NodeDef list) =
        nodes.AddRange(newNodes)

    /// All registered nodes.
    member _.AllNodes = nodes |> Seq.toList

    /// Nodes grouped by (HookPoint, Timing), in registration order.
    member _.ByHook() : Map<HookPoint * Timing, NodeDef list> =
        nodes
        |> Seq.groupBy (fun n -> n.Hook, n.Timing)
        |> Seq.map (fun (key, ns) -> key, ns |> Seq.toList)
        |> Map.ofSeq

    /// Validate state key dependencies across all registered nodes.
    member _.Validate() : ValidationReport =
        // Collect all reads and writes per key
        let writers = Dictionary<string, ResizeArray<string>>()
        let readers = Dictionary<string, ResizeArray<string>>()

        for n in nodes do
            for w in n.Writes do
                if not (writers.ContainsKey(w)) then writers.[w] <- ResizeArray()
                writers.[w].Add(n.Name)
            for r in n.Reads do
                if not (readers.ContainsKey(r)) then readers.[r] <- ResizeArray()
                readers.[r].Add(n.Name)

        // All unique keys
        let allKeys =
            Seq.append (writers.Keys) (readers.Keys)
            |> Seq.distinct
            |> Seq.toList

        let validations =
            allKeys
            |> List.map (fun key ->
                let ws = match writers.TryGetValue(key) with true, v -> v |> Seq.toList | _ -> []
                let rs = match readers.TryGetValue(key) with true, v -> v |> Seq.toList | _ -> []
                match ws, rs with
                | [], rs -> OrphanedReader(key, rs)
                | ws, _ when ws.Length > 1 ->
                    // Write conflict: multiple nodes on same hook writing same key
                    // For now, flag all multi-writer keys (v1 doesn't distinguish same-hook vs cross-hook)
                    WriteConflict(key, ws)
                | ws, rs -> Ok(key, ws, rs))

        let hasWarnings =
            validations |> List.exists (fun v ->
                match v with Ok _ -> false | _ -> true)

        let hooks =
            nodes
            |> Seq.map (fun n -> n.Hook, n.Timing)
            |> Seq.distinct
            |> Seq.length

        { TotalNodes = nodes.Count
          TotalHooks = hooks
          Keys = validations
          HasWarnings = hasWarnings }

    /// Format validation report as log lines.
    member this.FormatReport() : string list =
        let report = this.Validate()
        let lines = ResizeArray<string>()

        lines.Add(sprintf "Registered %d nodes across %d hooks" report.TotalNodes report.TotalHooks)

        // List nodes by hook
        for kv in this.ByHook() do
            let hook, timing = kv.Key
            let ns = kv.Value
            lines.Add(sprintf "  %A.%A:" hook timing)
            for n in ns do
                lines.Add(sprintf "    %s (reads: [%s], writes: [%s])"
                    n.Name
                    (String.concat ", " n.Reads)
                    (String.concat ", " n.Writes))

        // Key validations
        for v in report.Keys do
            match v with
            | Ok(key, ws, rs) ->
                lines.Add(sprintf "  OK  '%s': written by [%s], read by [%s]"
                    key (String.concat ", " ws) (String.concat ", " rs))
            | OrphanedReader(key, rs) ->
                lines.Add(sprintf "  WARN '%s': read by [%s] but NO WRITER installed"
                    key (String.concat ", " rs))
            | WriteConflict(key, ws) ->
                lines.Add(sprintf "  WARN '%s': written by MULTIPLE nodes [%s]"
                    key (String.concat ", " ws))

        if not report.HasWarnings then
            lines.Add("  No orphaned readers, no write conflicts")

        lines |> Seq.toList
