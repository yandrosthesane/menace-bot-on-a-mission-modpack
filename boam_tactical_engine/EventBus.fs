/// Simple event bus for synchronizing auto-navigation with game events.
/// Hooks push events, auto-navigate awaits them. Thread-safe via SemaphoreSlim.
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
    | MovementFinished of actor: string * tileX: int * tileZ: int
    | ActiveActorChanged of actor: string * faction: int * round: int * x: int * z: int

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
    | MovementFinished (a, x, z) -> sprintf "MovementFinished(%s (%d,%d))" a x z
    | ActiveActorChanged (a, f, r, x, z) -> sprintf "ActiveActorChanged(%s f=%d r=%d (%d,%d))" a f r x z

/// The event bus — single instance shared between hooks and auto-navigate.
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
        let mutable result: GameEvent option = None

        while result.IsNone do
            let! _ = signal.WaitAsync()
            let mutable ok, evt = queue.TryDequeue()
            while ok do
                if result.IsNone && predicate evt then
                    result <- Some evt
                else
                    pending.Enqueue(evt)
                let next = queue.TryDequeue()
                ok <- fst next
                evt <- snd next

        // Re-queue unmatched events
        let mutable ok, requeue = pending.TryDequeue()
        while ok do
            queue.Enqueue(requeue)
            signal.Release() |> ignore
            let next = pending.TryDequeue()
            ok <- fst next
            requeue <- snd next

        return result.Value
    }

    /// Drain all pending events.
    member _.Clear() =
        while queue.TryDequeue() |> fst do ()
        while signal.CurrentCount > 0 do
            signal.Wait(0) |> ignore
