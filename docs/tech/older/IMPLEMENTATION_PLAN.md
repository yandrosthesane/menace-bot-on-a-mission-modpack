# BOAM — Implementation Plan

Small steps. Each step is deployable and testable on its own.

---

## Step 1 — F# mod that logs on init

**Goal:** Prove F# compilation and loading works end-to-end.

- `Domain.fs`: define a simple type (`NodeDef` record with `Name: string` and `Hook: string`)
- `BoamPlugin.fs`: create a `NodeDef` value, log it with MelonLogger
- Deploy, launch game, confirm `[BOAM] Registered node: Test.Hello on OnTurnStart` appears in log

**What we learn:** F# types work at runtime under MelonLoader/Il2Cpp. The modkit compiles and deploys an F# modpack.

---

## Step 2 — Registry and node registration API

**Goal:** Modders can define and register nodes. Framework logs the merged graph.

- `StateKey.fs`: `StateKey<'t>` type with name and lifetime enum (`PerSession`, `PerRound`, `PerFaction`)
- `Node.fs`: `NodeDef` record — name, hook (string for now), timing (Prefix/Postfix), reads/writes as `string list`, run as `obj -> unit` (placeholder)
- `Registry.fs`: `BOAM.Register(nodes)`, stores in a `Dictionary<string, NodeDef list>` grouped by hook
- `BoamPlugin.fs`: on init, register a few dummy nodes, call `Registry.LogGraph()` which prints the merged node list
- Console command `boam status` prints registered nodes

**What we learn:** Registration API works. Multiple calls to `Register` merge correctly.

---

## Step 3 — Validation

**Goal:** Framework detects orphaned readers and write conflicts at registration time.

- `Registry.fs`: after all registrations, scan reads/writes across all nodes
  - Warn if a reads key has no writer
  - Warn if two nodes on the same hook write the same key
- Log validation results to MelonLogger
- Test: register nodes with a deliberate missing writer, confirm warning appears

**What we learn:** Cross-mod dependency detection works.

---

## Step 4 — Hook a real game method (OnTurnStart)

**Goal:** BOAM Harmony patch fires on AI turn, invokes registered nodes.

- `HarmonyPatches.cs` (C#): `[HarmonyPatch(typeof(AIFaction), nameof(AIFaction.OnTurnStart))]` prefix
- Patch calls into F# `Walker.Run("OnTurnStart", Prefix, faction, actor)`
- `Walker.fs`: iterates registered nodes for this hook, calls each node's `run` function with a minimal context
- `NodeContext.fs`: holds faction index and actor reference — just enough to log
- Test node: logs `[BOAM] OnTurnStart.Prefix fired for faction {idx}` — confirms the hook-to-F# bridge works

**What we learn:** C# Harmony patch can call F# functions. Walker executes nodes in order.

---

## Step 5 — State store

**Goal:** Nodes can read/write typed state that persists across hooks.

- `StateStore.fs`: `Dictionary<string, obj>` with `Read<'t>(key)` and `Write<'t>(key, value)` methods
- `NodeContext.fs`: gains `Read` and `Write` wrappers around the store
- Lifetime management: clear PerRound keys on `OnRoundStart`, clear PerFaction keys on faction change
- Test: node A on OnTurnStart writes a value, node B on OnTurnStart reads it — confirm data flows

**What we learn:** Cross-node state works. Lifetime cleanup works.

---

## Step 6 — Score capture (tile scores CSV)

**Goal:** Dump tile scores to CSV for each AI actor.

- `HarmonyPatches.cs`: add `[HarmonyPatch(typeof(ConsiderZones), "Evaluate")]` postfix — capture `TileScore` fields (UtilityScore, SafetyScore, DistanceScore, FinalScore) per tile per actor
- Accumulate scores in a per-actor buffer during the evaluation phase
- On turn end (or agent selection): flush buffer to `Mods/BOAM/scores/round_NN_factionN_ActorName_tiles.csv`
- Console command `boam capture on|off`
- No BOAM nodes needed yet — this is pure observation of the game's existing scores

**What we learn:** We can read tile scores from inside ConsiderZones. CSV output works. We see what the vanilla AI scores look like.

---

## Step 7 — Score capture (behavior scores CSV)

**Goal:** Dump behavior scores alongside tile scores.

- `HarmonyPatches.cs`: add patch on `Agent.Evaluate` or `Behavior.Evaluate` postfix — capture behavior type, score, target
- Flush to `*_behaviors.csv` alongside the tiles CSV
- Same `boam capture on|off` toggle

**What we learn:** We can read behavior scores. We have the full picture: where + what.

---

## Step 8 — PNG heatmap export

**Goal:** Render tile scores as a grid image.

- `HeatmapRenderer.fs` (or C#): takes tile score buffer + map dimensions, produces a `Texture2D`, calls `EncodeToPNG()`
- Color gradient: `Color.Lerp(cold, hot, normalized)` per tile
- Output alongside CSVs: `*_final.png`
- Same grid dimensions as TacticalMap — compositable

**What we learn:** PNG rendering works. Visual output matches expectations.

---

## Step 9 — First real node (read-only observer)

**Goal:** A BOAM node runs during ConsiderZones and reads tile scores without modifying them.

- Wire `Walker.Run("ConsiderZones", Postfix, ...)` into the ConsiderZones Harmony patch
- `NodeContext.fs`: expose `TileScore` (read-only) for ConsiderZones hooks
- Test node: reads UtilityScore, logs when it's above a threshold
- Confirm: node runs per-tile, sees correct scores, game behavior unchanged

**What we learn:** BOAM nodes can run in the ConsiderZones hot path without breaking the game. Performance baseline established.

---

## Step 10 — First real node (write)

**Goal:** A BOAM node modifies UtilityScore and the AI responds.

- `NodeContext.fs`: expose `AddUtilityScore(delta)` for ConsiderZones hooks
- Test node: adds a fixed bonus to tiles near a hardcoded position
- Deploy, play round, confirm AI moves toward the boosted tiles
- Compare CSVs: run without the node vs with — the boosted tiles should have higher utility/final scores

**What we learn:** Score mutation works end-to-end. The modder workflow (compare CSVs) is validated.

---

## After Step 10

The framework is usable. A modder can:

1. Define nodes in F# with typed state keys
2. Register them — framework validates dependencies
3. Nodes run on game hooks, read/write state, modify scores
4. Capture tile + behavior scores to CSV/PNG for comparison

From here: re-express BooAPeek as BOAM nodes (proving the architecture), add tracing, add more hooks.
