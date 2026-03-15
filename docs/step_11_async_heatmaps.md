# Step 11: Async Heatmap Rendering

**Status:** Planned

## Problem

Heatmap generation currently blocks both the game's AI evaluation thread (C# bridge) and the engine's HTTP handler (F# engine). With ~25 units per round, this creates visible lag during AI turns.

## Solution: Two-Level Async

### 1. C# Bridge: Non-blocking POST

Change `Patch_PostProcessTileScores` to fire the tile-scores POST on a background thread instead of blocking the AI evaluation.

```csharp
// Before (blocking):
var response = EngineClient.Post("/hook/tile-scores", payload);

// After (fire and forget):
var json = payload; // capture for closure
ThreadPool.QueueUserWorkItem(_ => EngineClient.Post("/hook/tile-scores", json));
```

The AI thread continues immediately — no performance impact on the game.

### 2. F# Engine: Render Queue

The tile-scores route handler enqueues a render job and returns HTTP 200 immediately. A `MailboxProcessor` processes render jobs one at a time in the background.

```fsharp
type RenderJob = {
    Payload: TileScoresPayload
    OutputDir: string
    Label: string
}

let private renderAgent = MailboxProcessor.Start(fun inbox ->
    let rec loop () = async {
        let! job = inbox.Receive()
        try
            HeatmapRenderer.renderAll ...
        with ex ->
            logWarn (sprintf "Render failed: %s" ex.Message)
        return! loop ()
    }
    loop ()
)
```

Benefits:
- Route handler returns instantly — doesn't block other hook processing
- Renders one PNG at a time — no CPU saturation
- Natural backpressure — if rendering is slow, jobs queue up but the game is unaffected
- `MailboxProcessor` is single-threaded — no concurrency issues with ImageSharp

### 3. Config

`config.json` already has `"heatmaps": false`. Change to `true` when ready.

## Files to Change

| File | Change |
|------|--------|
| `src/AiObservationPatches.cs` | Wrap tile-scores POST in `ThreadPool.QueueUserWorkItem` |
| `boam_tactical_engine/Routes.fs` | Enqueue render job instead of inline rendering |
| `boam_tactical_engine/HeatmapRenderer.fs` | Add `RenderQueue` module with `MailboxProcessor` |

## Impact

- Zero game performance impact — AI evaluation is never blocked
- Engine remains responsive to other hooks during rendering
- Heatmaps appear progressively as rendering completes (not all at once)
