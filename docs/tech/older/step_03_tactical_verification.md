# Step 3: Tactical Verification -- Sidecar Hooks Confirmed

**Date:** 2026-03-13
**Status:** VERIFIED

## What Was Verified

The full pipeline from game launch to sidecar `on-turn-start` hook firing works end-to-end:

1. **Game launch** -- Steam + MelonLoader + BOAM plugin loads
2. **Sidecar discovery** -- Plugin finds sidecar at `http://127.0.0.1:7660/status` on startup
3. **Save load** -- `continuesave` command loads into MissionPreparation scene
4. **Screen navigation** -- `planmission` uses `UIManager.OpenScreen("MissionPrepUIScreen")` to navigate
5. **Mission launch** -- `startmission` calls `MissionPrepUIScreen.LaunchMission()` to enter Tactical
6. **OnTurnStart hook fires** -- BOAM's Harmony prefix on `AIFaction.OnTurnStart` fires per-unit
7. **Sidecar responds** -- Each hook call POSTs to sidecar, receives 68-byte response

## Log Evidence

```
[BOAM] Tactical ready, sidecar hooks active
[BOAM] on-turn-start f3: sidecar OK (68b)   <- AlliedLocalForces faction
[BOAM] on-turn-start f7: sidecar OK (68b)   <- Wildlife faction
[endturn] EndTurn returned: True
```

Hooks fire for multiple factions (f3=AlliedLocalForces, f7=Wildlife) and for each unit
within a faction. Bridge remains stable through multiple complete rounds.

## Critical Fix: Main-Thread Dispatch for Tactical Commands

`endturn`, `skipai`, and `move` DevConsole commands were calling `TacticalController` /
`EntityMovement` methods directly on the bridge HTTP thread, causing native crashes
(exit code 52 + connection refused).

**Fix:** Wrapped all three with `DevConsole.EnqueueMainThread()`:

```csharp
RegisterCommand("endturn", "", "End the current turn (queued for main thread)", args =>
{
    EnqueueMainThread(() =>
    {
        var result = TacticalController.EndTurn();
        SdkLogger.Msg($"[endturn] EndTurn returned: {result}");
    });
    return "endturn queued -- check logs for result";
});
```

Commands now return "queued" immediately; results are logged asynchronously.

## Working Automated Test Sequence

```bash
# From Title screen:
curl -s http://127.0.0.1:7655/cmd -d 'continuesave'     # -> MissionPreparation (12s)
curl -s http://127.0.0.1:7655/cmd -d 'planmission 0'     # -> Opens MissionPrepUIScreen (5s)
curl -s http://127.0.0.1:7655/cmd -d 'startmission'      # -> Tactical scene (15s)
curl -s http://127.0.0.1:7655/cmd -d 'endturn'            # x4 to end all player units
# Wait 15s for AI factions to process
# Check logs: grep "BOAM\|endturn" .../MelonLoader/Latest.log
```

## Key Discovery: UIManager.OpenScreen

Instead of trying to invoke compiler-generated lambda callbacks on UI buttons (unreliable,
wrong lambda triggered ABORT instead of PLAN MISSION), we discovered `UIManager.OpenScreen`:

```csharp
var uiManager = FindObjectsOfType<UIManager>()[0];
uiManager.OpenScreen("MissionPrepUIScreen", null, false, false);
```

This is the game's internal screen navigation API. See `docs/UI_DISCOVERY.md` for full details.

## Next Steps

- [ ] Enrich `on-turn-start` payload with actual faction/unit state
- [ ] Sidecar graph engine processes state and returns ghost commands
- [ ] Apply ghost tile score modifiers before AI evaluation
