/// Typed state keys for cross-node data flow.
/// Each key has a name, a type, and a lifetime that controls when it's cleared.
module BOAM.Sidecar.StateKey

/// When a state key's value is automatically cleared.
type Lifetime =
    | PerSession    // persists for the entire tactical session
    | PerRound      // cleared at the start of each round
    | PerFaction    // cleared when a new faction starts its turn

/// A typed state key. The type parameter is erased at runtime but enforced at compile time.
type StateKey<'t> = {
    Name: string
    Lifetime: Lifetime
}

/// Create a per-session state key.
let perSession<'t> name : StateKey<'t> = { Name = name; Lifetime = PerSession }

/// Create a per-round state key.
let perRound<'t> name : StateKey<'t> = { Name = name; Lifetime = PerRound }

/// Create a per-faction state key.
let perFaction<'t> name : StateKey<'t> = { Name = name; Lifetime = PerFaction }

/// Untyped key info for registry/validation (erases the type parameter).
type StateKeyInfo = {
    Name: string
    Lifetime: Lifetime
}

let toInfo (key: StateKey<'t>) : StateKeyInfo =
    { Name = key.Name; Lifetime = key.Lifetime }
