# Step 1 — F# Mod That Logs a Successful Registration

## Goal

Prove that an F# modpack compiles, deploys, loads under MelonLoader, and can define + use F# types at runtime.

## What to implement

### Domain.fs

Define the minimal types that will become the real registration API in later steps:

```fsharp
module BOAM.Domain

/// When a node fires relative to the hooked method.
type Timing = Prefix | Postfix

/// Which game hook point a node binds to.
type HookPoint =
    | OnTurnStart
    | OnRoundStart
    | ConsiderZones
    | SetTile
    | OnDamageReceived

/// A registered node — the unit of work in BOAM.
type NodeDef = {
    Name: string
    Hook: HookPoint
    Timing: Timing
    Reads: string list
    Writes: string list
}
```

These are real types we'll keep and extend. No throwaway code.

### BoamPlugin.fs

Create a few `NodeDef` values and log them:

```fsharp
type BoamPlugin() =
    interface IModpackPlugin with
        member _.OnInitialize(logger, harmony) =
            logger.Msg "BOAM initialized"

            // Define test nodes using the real types
            let nodes = [
                { Name = "BooAPeek.Filter"
                  Hook = OnTurnStart
                  Timing = Prefix
                  Reads = ["Opponents"]
                  Writes = ["Visible"; "Removed"] }

                { Name = "BooAPeek.InjectGhost"
                  Hook = ConsiderZones
                  Timing = Postfix
                  Reads = ["Ghost"; "Calibration"]
                  Writes = ["UtilityScore"] }
            ]

            // Log each node — proves F# types work at runtime
            for n in nodes do
                logger.Msg(sprintf "[BOAM] Registered node: %s on %A.%A (reads: %s, writes: %s)"
                    n.Name n.Hook n.Timing
                    (String.concat ", " n.Reads)
                    (String.concat ", " n.Writes))

            logger.Msg(sprintf "[BOAM] %d nodes registered" nodes.Length)
```

## Files changed

| File | Change |
|------|--------|
| `src/Domain.fs` | Replace stub with `Timing`, `HookPoint`, `NodeDef` types |
| `src/BoamPlugin.fs` | Create `NodeDef` values, log them |
| `modpack.json` | No changes needed (sources and references already correct) |

## Pass criteria

Deploy and launch the game. In MelonLoader log (`Latest.log`), confirm:

```
[BOAM] Registered node: BooAPeek.Filter on OnTurnStart.Prefix (reads: Opponents, writes: Visible, Removed)
[BOAM] Registered node: BooAPeek.InjectGhost on ConsiderZones.Postfix (reads: Ghost, Calibration, writes: UtilityScore)
[BOAM] 2 nodes registered
```

## What this proves

1. F# discriminated unions (`HookPoint`, `Timing`) compile and work at runtime under MelonLoader/Il2Cpp
2. F# records (`NodeDef`) with list fields work
3. `sprintf` with `%A` formatting on DUs works (produces human-readable output)
4. The modkit's F# compilation pipeline produces a working DLL
5. The types defined in `Domain.fs` are usable from `BoamPlugin.fs` (file ordering works)

## What this does NOT prove yet

- No Harmony patches (step 4)
- No game hook integration (step 4)
- No state persistence (step 5)
- No score capture (step 6)
- No actual AI modification (step 10)
