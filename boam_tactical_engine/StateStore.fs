/// In-memory key-value store for cross-node state.
/// Values are boxed (obj) at runtime; type safety is enforced by StateKey<'t> at call sites.
module BOAM.TacticalEngine.StateStore

open System.Collections.Generic
open BOAM.TacticalEngine.StateKey

type StateStore() =
    let store = Dictionary<string, obj>()
    let lifetimes = Dictionary<string, Lifetime>()

    /// Read a typed value. Returns None if the key hasn't been written.
    member _.Read<'t>(key: StateKey<'t>) : 't option =
        match store.TryGetValue(key.Name) with
        | true, v -> Some (v :?> 't)
        | _ -> None

    /// Read a typed value with a default fallback.
    member this.ReadOrDefault<'t>(key: StateKey<'t>, defaultValue: 't) : 't =
        this.Read(key) |> Option.defaultValue defaultValue

    /// Write a typed value.
    member _.Write<'t>(key: StateKey<'t>, value: 't) =
        store.[key.Name] <- box value
        lifetimes.[key.Name] <- key.Lifetime

    /// Update a value using a transform function. If the key doesn't exist, starts from defaultValue.
    member this.Update<'t>(key: StateKey<'t>, defaultValue: 't, f: 't -> 't) =
        let current = this.ReadOrDefault(key, defaultValue)
        this.Write(key, f current)

    /// Clear all keys with the given lifetime.
    member _.ClearLifetime(lifetime: Lifetime) =
        let toRemove =
            lifetimes
            |> Seq.filter (fun kv -> kv.Value = lifetime)
            |> Seq.map (fun kv -> kv.Key)
            |> Seq.toList
        for k in toRemove do
            store.Remove(k) |> ignore
            lifetimes.Remove(k) |> ignore

    /// Clear everything.
    member _.ClearAll() =
        store.Clear()
        lifetimes.Clear()

    /// Get all stored key names and their lifetimes (for diagnostics).
    member _.Dump() : (string * Lifetime) list =
        lifetimes
        |> Seq.map (fun kv -> kv.Key, kv.Value)
        |> Seq.toList
