# Step 1 — Current Status

## State: Deployed, awaiting game restart to verify

## What happened

1. Implemented `Domain.fs` (Timing, HookPoint, NodeDef types) and `BoamPlugin.fs` (creates 2 test nodes, logs them)
2. First deploy failed — MCP was compiling F# files as C# because it hadn't been rebuilt since F# support was added
3. Rebuilt MCP (`dotnet build src/Menace.Modkit.Mcp -c Debug`) — F# compilation worked
4. Second deploy failed at runtime — `FSharp.Core.dll` was the net10.0 version (from MCP's AppContext.BaseDirectory), incompatible with MelonLoader's net6.0 runtime
5. Fixed `FSharpCompilationService.FindFSharpCore()` to prefer the `netstandard2.1` variant from NuGet cache (`~/.nuget/packages/fsharp.core/9.0.101/lib/netstandard2.1/`)
6. Rebuilt MCP, redeployed — correct FSharp.Core (2,318,136 bytes, netstandard2.1) now in `Mods/BOAM/dlls/`

## Fix applied

**File:** `src/Menace.Modkit.App/Services/FSharpCompilationService.cs` — `FindFSharpCore()` method

**Before:** looked next to MCP binary (finds net10.0 copy, wrong TFM for MelonLoader)

**After:** searches NuGet cache for `netstandard2.1` variant first, falls back to local copy

## What to verify on next game launch

Look for these lines in `MelonLoader/Latest.log`:

```
[BOAM] Registered node: BooAPeek.Filter on OnTurnStart.Prefix (reads: Opponents, writes: Visible, Removed)
[BOAM] Registered node: BooAPeek.InjectGhost on ConsiderZones.Postfix (reads: Ghost, Calibration, writes: UtilityScore)
[BOAM] 2 nodes registered
```

**If it still fails**, check for:
- `FSharp.Core` load errors (version/format mismatch)
- `Some types could not be loaded` (F# DU types not resolving)

## Files changed this session

| File | Change |
|------|--------|
| `BOAM-modpack/src/Domain.fs` | Timing, HookPoint DUs + NodeDef record |
| `BOAM-modpack/src/BoamPlugin.fs` | Creates 2 test NodeDefs, logs them |
| `BOAM-modpack/docs/DESIGN.md` | Updated with chosen architecture + doc index |
| `BOAM-modpack/docs/IMPLEMENTATION_PLAN.md` | 10-step build plan |
| `BOAM-modpack/docs/v1_minimal_spec.md` | v1 scope, API, tracing, score export |
| `BOAM-modpack/docs/AI_TERMINAL_SCORES.md` | Catalog of all pilotable AI scores |
| `BOAM-modpack/docs/candidate_evaluation.md` | 4 candidates evaluated against 3 mods |
| `BOAM-modpack/docs/candidate_merged_behavior_graph.md` | Chosen architecture design |
| `BOAM-modpack/docs/step_01_fsharp_proof.md` | Step 1 implementation spec |
| `BOAM-modpack/docs/step_01_status.md` | This file |
| `MenaceAssetPacker/src/.../FSharpCompilationService.cs` | Fixed FindFSharpCore() for netstandard2.1 |

## Next step after verification

If step 1 passes → step 2: Registry + `boam status` console command (see `IMPLEMENTATION_PLAN.md`)
