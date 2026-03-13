# Step 2: HTTP Checkpoint Architecture

## Previous Design (Step 1: Length-Prefixed TCP)

The initial sidecar design used a persistent TCP connection with custom framing (4-byte LE length prefix + UTF-8 JSON). The C# plugin opened a socket to the sidecar on the Title screen, maintained the connection, and exchanged typed messages (Ping/Pong, FactionState/GhostCommands, etc.).

This worked but had unnecessary complexity:
- Custom wire protocol (length-prefixed framing) that needed matching implementations in C# and F#
- Persistent connection lifecycle management (connect, reconnect on crash, heartbeat)
- A `Protocol.fs` module duplicating message type definitions across both sides
- Not testable with standard tools — you'd need a custom client to send length-prefixed messages

## The Insight: We Already Have a Working HTTP Server Pattern

The game bridge on port 7655 already proves that **HTTP over TCP loopback between Wine and native Linux works perfectly**. It handles JSON request/response, runs inside MelonLoader's Wine/.NET 6.0 process, and is callable from any Linux process via `curl`.

The sidecar doesn't need a custom protocol — it just needs to be an HTTP server, the same pattern as the bridge.

## The Second Insight: Checkpoints, Not Polling

The original design had the plugin push state to the sidecar asynchronously, then poll or wait for responses. But Harmony prefix patches naturally provide a **synchronous checkpoint**: the game's AI evaluation is paused inside the prefix, waiting for the patch method to return.

If the prefix patch makes a **blocking HTTP call** to the sidecar, the game waits for the sidecar to process the graph and return modifications. No polling, no race conditions, no async coordination. The game's own call stack is the synchronization mechanism.

## Revised Architecture

### Overview

```
Game AI calls OnTurnStart()
       │
       ▼
Harmony PREFIX patch fires
       │
       ▼
Plugin: POST http://127.0.0.1:7660/hook/on-turn-start
       │         { faction, units, opponents, turn }
       │
       │  ┌─── Sidecar receives request
       │  │    Evaluates behavior graph
       │  │    (optionally) GET http://127.0.0.1:7655/actors
       │  │       for additional state from game bridge
       │  │    Returns { ghosts, scoreModifiers }
       │  └──►
       │
       ▼
Plugin applies ghost commands + score modifiers
Prefix patch returns → game continues with modified state
```

### Why HTTP + Checkpoints

| Concern | Length-Prefixed TCP | HTTP Checkpoints |
|---------|-------------------|------------------|
| Wire format | Custom framing code in both C# and F# | Standard HTTP — `System.Net.Http` / ASP.NET minimal API |
| Testing | Custom test client needed | `curl -X POST http://127.0.0.1:7660/hook/on-turn-start -d '{...}'` |
| Connection lifecycle | Connect/reconnect/heartbeat logic | Stateless — each hook is an independent request |
| Synchronization | Async messages, need correlation IDs | Blocking HTTP call = natural checkpoint |
| Existing precedent | None in this project | Game bridge (port 7655) uses this exact pattern |
| Debugging | Binary framing, need packet capture | Plain HTTP, inspectable with any tool |
| Sidecar crash | Plugin must detect, reconnect, replay | Request fails, plugin catches exception, AI runs vanilla |

### Endpoint Design

The sidecar exposes one endpoint per game hook:

```
POST /hook/on-turn-start     → GhostCommands + ScoreModifiers
POST /hook/consider-zones    → TileScoreModifiers
POST /hook/round-end         → TraceExport (optional)
GET  /status                 → { version, graphs, uptime }
POST /shutdown               → sidecar exits gracefully
```

Each endpoint receives game state as JSON in the request body and returns modifications as JSON in the response body. Standard HTTP status codes: 200 = success, 204 = no modifications, 500 = graph error (plugin logs and lets AI run vanilla).

### Checkpoint Pattern in Harmony Patches

```csharp
[HarmonyPatch(typeof(AIFaction), nameof(AIFaction.OnTurnStart))]
[HarmonyPrefix]
static void OnTurnStartPrefix(AIFaction __instance)
{
    // Extract state from Il2Cpp objects
    var state = ExtractFactionState(__instance);

    // BLOCKING call — game waits here
    var response = _httpClient.PostAsync(
        "http://127.0.0.1:7660/hook/on-turn-start",
        new StringContent(state, Encoding.UTF8, "application/json")
    ).Result;  // .Result blocks the AI thread

    if (response.IsSuccessStatusCode)
    {
        var mods = JsonSerializer.Deserialize<Modifications>(
            response.Content.ReadAsStringAsync().Result);
        ApplyGhosts(mods.Ghosts);
        ApplyScoreModifiers(mods.ScoreModifiers);
    }
    // else: sidecar unavailable or errored — AI runs vanilla
}
```

The `.Result` call is intentionally blocking. This is safe because:
- We're inside a Harmony prefix on the AI thread, not the main/render thread
- The AI evaluation already takes 5-10 seconds — a sub-millisecond HTTP call is invisible
- If the sidecar is down, the HTTP timeout fires and the game continues unmodified

### Sidecar as HTTP Server

The F# sidecar becomes a minimal HTTP server using ASP.NET minimal APIs:

```fsharp
let builder = WebApplication.CreateBuilder()
let app = builder.Build()

app.MapPost("/hook/on-turn-start", fun (state: FactionState) ->
    let result = GraphEngine.evaluate OnTurnStart state
    Results.Ok(result))

app.MapGet("/status", fun () ->
    {| version = "0.1.0"; status = "ready" |})

app.Run("http://127.0.0.1:7660")
```

This gives us:
- Routing, JSON serialization/deserialization, error handling — all built-in
- Swagger/OpenAPI if we want it later
- Middleware pipeline for logging, tracing, etc.
- Battle-tested HTTP stack, not custom framing code

### Sidecar Can Query the Game Bridge

When the sidecar needs game state beyond what the plugin sends in the hook payload, it can query the existing game bridge:

```fsharp
// Inside a hook handler, if we need extra data
let! actors = httpClient.GetFromJsonAsync<ActorList>("http://127.0.0.1:7655/actors")
```

This is safe during a checkpoint because the game is blocked in the Harmony prefix — the bridge's HTTP thread is still free to serve requests. The sidecar gets the data it needs, processes the graph, and responds to the plugin.

### Connection Lifecycle

There is no persistent connection to manage:

1. **Sidecar starts** — manually, via deploy script, or via a launcher wrapper (Wine can't launch native Linux processes directly)
2. **Plugin fires hook** — makes HTTP request to sidecar
3. **Sidecar processes** — returns response
4. **Plugin applies result** — game continues
5. **Game exits** — plugin sends `POST /shutdown` as a courtesy (sidecar can also just stay running for hot-reload)

If the sidecar isn't running, HTTP requests fail immediately. The plugin catches the exception and logs a warning. No reconnection logic, no heartbeat, no state to clean up.

### What This Replaces

From the Step 1 TCP design, the following are **no longer needed**:

- `Protocol.fs` — custom message types and length-prefixed framing
- `Ping/Pong` handshake — replaced by `GET /status`
- Persistent TCP connection — replaced by stateless HTTP
- Background connection thread in C# plugin — replaced by blocking HTTP in Harmony patches
- `MessageType` enum — replaced by URL routing (`/hook/on-turn-start`, etc.)

## Impact on Other Design Documents

- **`sidecar_architecture.md`**: Wire protocol section needs update (HTTP instead of length-prefixed TCP). Performance analysis still holds — HTTP adds ~0.1ms overhead vs raw TCP, still <0.2% of AI turn time.
- **`DESIGN.md`**: "Runtime Architecture" section references sidecar — no change needed, the split is the same.
- **`IMPLEMENTATION_PLAN.md`**: Step 1 becomes simpler (no custom protocol, use standard HTTP libraries).

## Related Documents

| Document | Content |
|----------|---------|
| `sidecar_architecture.md` | Sidecar split rationale, game-agnostic vision, performance analysis |
| `step_01_fsharp_failure.md` | Why FSharp.Core can't load — the constraint that led to the sidecar |
| `DESIGN.md` | BOAM graph engine design (unchanged by transport choice) |
