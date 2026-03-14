/// Simple event bus for synchronizing replay with game events.
/// Hooks push events, replay awaits them. Thread-safe via SemaphoreSlim.
/// No timeouts — waits until a matching event arrives.
module BOAM.TacticalEngine.EventBus

open System
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent

/// An event received from the game bridge.
type GameEvent =
    | SceneChanged of scene: string
    | BattleStarted
    | TacticalReady
    | PreviewReady
    | BattleEnded
    | PlayerAction of round: int * actor: string * actionType: string * tileX: int * tileZ: int * skill: string
    | TurnStart of faction: int * round: int
    | MovementFinished of faction: int * actorId: int * tileX: int * tileZ: int
    | ActiveActorChanged of actorId: int * template: string * faction: int * x: int * z: int

/// Format an event for logging.
let formatEvent (evt: GameEvent) =
    match evt with
    | SceneChanged s -> sprintf "SceneChanged(%s)" s
    | BattleStarted -> "BattleStarted"
    | TacticalReady -> "TacticalReady"
    | PreviewReady -> "PreviewReady"
    | BattleEnded -> "BattleEnded"
    | PlayerAction (r, a, t, x, z, s) -> sprintf "PlayerAction(r=%d %s %s (%d,%d) %s)" r a t x z s
    | TurnStart (f, r) -> sprintf "TurnStart(f=%d r=%d)" f r
    | MovementFinished (f, id, x, z) -> sprintf "MovementFinished(f=%d id=%d (%d,%d))" f id x z
    | ActiveActorChanged (id, t, f, x, z) -> sprintf "ActiveActorChanged(id=%d %s f=%d (%d,%d))" id t f x z

/// The event bus — single instance shared between hooks and replay.
type Bus(log: string -> unit) =
    let queue = ConcurrentQueue<GameEvent>()
    let signal = new SemaphoreSlim(0)

    /// Push an event (called by hook handlers).
    member _.Push(evt: GameEvent) =
        log (sprintf "[EventBus] <- %s" (formatEvent evt))
        queue.Enqueue(evt)
        signal.Release() |> ignore

    /// Wait for the next event matching a predicate. Blocks until found.
    member _.WaitFor(predicate: GameEvent -> bool) = task {
        let pending = ConcurrentQueue<GameEvent>()

        let mutable result = Unchecked.defaultof<GameEvent>
        let mutable found = false
        while not found do
            let! _ = signal.WaitAsync()
            let mutable evt = Unchecked.defaultof<GameEvent>
            while queue.TryDequeue(&evt) do
                if not found && predicate evt then
                    result <- evt
                    found <- true
                else
                    pending.Enqueue(evt)

        // Re-queue unmatched events
        let mutable requeue = Unchecked.defaultof<GameEvent>
        while pending.TryDequeue(&requeue) do
            queue.Enqueue(requeue)
            signal.Release() |> ignore

        return result
    }

    /// Drain all pending events (call at replay start to clear stale events).
    member _.Clear() =
        let mutable evt = Unchecked.defaultof<GameEvent>
        while queue.TryDequeue(&evt) do ()
        while signal.CurrentCount > 0 do
            signal.Wait(0) |> ignore
