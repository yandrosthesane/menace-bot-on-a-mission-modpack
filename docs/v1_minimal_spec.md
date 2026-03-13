# BOAM v1 — Minimal Spec

## Scope

v1 is: nodes + state keys + merge validation + traced execution + score diffing.

No branching (paths), no extension points, no splice, no extensible branches. Just linear chains of nodes per hook, with declared state keys for cross-hook data flow. Each node does one thing. Conditional logic lives inside the node's `run` function — the graph doesn't model it yet.

## What a Modder Writes

```fsharp
module BooAPeek.Nodes

open BOAM

// Declare state keys with types and lifetimes
let lastSeen    = StateKey.perSession<LastSeenMap>   "BooAPeek.LastSeen"
let ghost       = StateKey.perFaction<GhostMap>      "BooAPeek.Ghost"
let calibration = StateKey.perRound<Calibration>     "BooAPeek.Calibration"

// Node functions — just functions
let filterOpponents (ctx: NodeContext) =
    let opponents = ctx.Read<OpponentList>(GameState.Opponents)
    let visible, removed = opponents |> List.partition (canSee ctx.Faction)
    ctx.Write(GameState.Visible, visible)
    ctx.Write(GameState.Removed, removed)

let createGhost (ctx: NodeContext) =
    let removed = ctx.Read(GameState.Removed)
    let seen = ctx.Read(lastSeen)
    for r in removed do
        match Map.tryFind r.ActorId seen with
        | Some pos -> ctx.Update(ghost, addGhost r.ActorId pos)
        | None     -> ()

let recordSighting (ctx: NodeContext) =
    let visible = ctx.Read(GameState.Visible)
    for v in visible do
        ctx.Update(lastSeen, Map.add v.ActorId v.Position)

let injectGhostBonus (ctx: NodeContext) =
    let ghosts = ctx.Read(ghost)
    let cal = ctx.Read(calibration)
    let tileScore = ctx.TileScore   // only available on ConsiderZones hooks
    let bonus = computeBonus ghosts cal tileScore.Tile
    ctx.AddUtilityScore(bonus)

// Register nodes — order within a hook is declaration order (explicit)
let nodes = [
    node "BooAPeek.Filter" {
        hook (OnTurnStart Prefix)
        reads  [ GameState.Opponents ]
        writes [ GameState.Visible; GameState.Removed ]
        run filterOpponents
    }
    node "BooAPeek.RecordOrGhost" {
        hook (OnTurnStart Prefix)
        reads  [ GameState.Visible; GameState.Removed; lastSeen ]
        writes [ lastSeen; ghost ]
        run (fun ctx ->
            recordSighting ctx
            createGhost ctx
        )
    }
    node "BooAPeek.InjectGhost" {
        hook (ConsiderZones Postfix)
        reads  [ ghost; calibration ]
        writes [ GameState.UtilityScore ]
        run injectGhostBonus
    }
]
```

```fsharp
// Plugin init
type BooAPeekPlugin() =
    interface IModpackPlugin with
        member _.Initialize(context) =
            BOAM.Register(BooAPeek.Nodes.nodes)
```

That's it. No graph topology to wire. No `next` pointers in v1. Nodes on the same hook run in declaration order. Cross-hook state flows through state keys.

## What the Framework Does

### 1. Merge

Collect all registered nodes, group by hook + timing.

```
OnTurnStart.Prefix:
  1. BooAPeek.Filter         (reads Opponents, writes Visible/Removed)
  2. BooAPeek.RecordOrGhost  (reads Visible/Removed/LastSeen, writes LastSeen/Ghost)
  3. UnderFire.Flee           (reads UnderFire, writes RoleData.Self)
  4. UnderFire.AlertNearby    (reads UnderFire, writes RoleData.Nearby)

ConsiderZones.Postfix:
  1. BooAPeek.InjectGhost     (reads Ghost/Calibration, writes UtilityScore)

OnDamageReceived.Postfix:
  1. UnderFire.Detect         (reads DamageEvent, writes UnderFire)

InflictDamage.Postfix:
  1. Potshot.ScoreGhostTiles  (reads Ghost/SkillRange, writes BehaviorScore)
```

### 2. Validate (at registration time, logged to MelonLoader console)

```
[BOAM] ✓ Registered 7 nodes across 4 hooks
[BOAM] ✓ State key 'BooAPeek.Ghost': written by BooAPeek.RecordOrGhost, read by BooAPeek.InjectGhost, Potshot.ScoreGhostTiles
[BOAM] ✓ State key 'UnderFire.Flagged': written by UnderFire.Detect, read by UnderFire.Flee, UnderFire.AlertNearby
[BOAM] ✓ No orphaned readers, no write conflicts
```

Or if Potshot is installed without BooAPeek:

```
[BOAM] ⚠ State key 'BooAPeek.Ghost': read by Potshot.ScoreGhostTiles but NO WRITER installed
[BOAM]   → Potshot.ScoreGhostTiles will receive empty state
```

### 3. Execute with Tracing

When `BOAM.TraceEnabled = true` (togglable via mod settings or console command), every hook invocation logs the full execution trace.

#### Trace output — OnTurnStart

```
[BOAM] ─── OnTurnStart.Prefix ─── Faction 2 "Raiders", Actor "Raider_03" ───
[BOAM]  → BooAPeek.Filter
[BOAM]      read  Opponents: 5 entries
[BOAM]      write Visible: 3 entries
[BOAM]      write Removed: 2 entries
[BOAM]      elapsed: 0.02ms
[BOAM]  → BooAPeek.RecordOrGhost
[BOAM]      read  Visible: 3 entries
[BOAM]      read  Removed: 2 entries
[BOAM]      read  BooAPeek.LastSeen: 1 entry
[BOAM]      write BooAPeek.LastSeen: 3 entries (+2 new)
[BOAM]      write BooAPeek.Ghost: 1 entry (+1 new: Actor_07 at (14,7))
[BOAM]      elapsed: 0.05ms
[BOAM]  → UnderFire.Flee
[BOAM]      read  UnderFire.Flagged: empty → SKIPPED (no data)
[BOAM]  → UnderFire.AlertNearby
[BOAM]      read  UnderFire.Flagged: empty → SKIPPED (no data)
[BOAM] ─── OnTurnStart.Prefix complete (0.08ms) ───
```

#### Trace output — ConsiderZones (per-tile, verbose mode only)

```
[BOAM] ─── ConsiderZones.Postfix ─── Faction 2, Actor "Raider_03", Tile (14,7) ───
[BOAM]  → BooAPeek.InjectGhost
[BOAM]      read  BooAPeek.Ghost: 1 entry
[BOAM]      read  BooAPeek.Calibration: spread=45.2
[BOAM]      write UtilityScore: 0.0 → 12.5 (+12.5)
```

ConsiderZones fires per-tile (hundreds of times), so this level of detail is opt-in via a separate `BOAM.TraceVerbose` flag. Default trace for ConsiderZones is a summary.

#### Trace output — Score Summary (after all hooks for one agent)

```
[BOAM] ═══ Score Summary ═══ Faction 2 "Raiders", Actor "Raider_03" ═══
[BOAM]
[BOAM]  Tile Scores (top 5):
[BOAM]   Tile (14, 7): Utility  0.0 → 12.5 (+12.5 ghost)  Safety  8.3  Distance  2.1  Final 42.1
[BOAM]   Tile (13, 8): Utility  0.0 →  0.0                 Safety 15.2  Distance  1.0  Final 38.0
[BOAM]   Tile (15, 6): Utility  0.0 →  0.0                 Safety  6.1  Distance  3.2  Final 21.4
[BOAM]   Tile (12, 9): Utility  0.0 →  0.0                 Safety 12.0  Distance  4.5  Final 18.3
[BOAM]   Tile (14, 8): Utility  0.0 →  0.0                 Safety  3.2  Distance  1.4  Final 12.1
[BOAM]
[BOAM]  BOAM Contributions:
[BOAM]   BooAPeek.InjectGhost: 1 tile affected, max bonus +12.5 at (14,7)
[BOAM]   Potshot.ScoreGhostTiles: 0 behaviors affected (no valid skills)
[BOAM]
[BOAM]  Behavior Scores:
[BOAM]   InflictDamage    85  (target: Player_01 at (16,5))
[BOAM]   Move             42  (target tile: (14,7), FinalScore 42.1)
[BOAM]   InflictSuppression 0 (no valid targets)
[BOAM]
[BOAM]  Before BOAM: best tile (13,8) Final 38.0, selected Move
[BOAM]  After BOAM:  best tile (14,7) Final 42.1, selected Move ← CHANGED by ghost bonus
[BOAM]
[BOAM] ═══════════════════════════════════════════════════════════════════
```

The "Before BOAM / After BOAM" diff is the key insight for modders — it shows exactly how their nodes changed the AI's decision.

### 4. Score Diffing Implementation

The framework captures scores at two points:

1. **Before BOAM nodes run** on each hook — snapshot the relevant scores
2. **After BOAM nodes run** — diff against snapshot

For ConsiderZones:
- Snapshot each tile's UtilityScore before BOAM nodes
- After BOAM nodes, record the delta per tile
- Accumulate deltas across all tiles for the summary

For Behaviors:
- Snapshot Behavior.Score before BOAM nodes on InflictDamage hooks
- After BOAM nodes, record which behaviors changed and by how much

The summary is emitted once per agent, after all hooks for that agent have completed (after behavior selection).

## What v1 Does NOT Include

| Feature | Why deferred |
|---------|-------------|
| `next` / `paths` (graph flow control) | v1 nodes are a flat ordered list per hook. Branching lives inside `run`. Add flow control when a mod actually needs the framework to model branches. |
| Branch extension (`extendPaths`) | Requires `paths` first. |
| Splice (`after` / `before`) | v1 ordering is declaration order within a mod. Cross-mod ordering is registration order. Add splice when cross-mod ordering conflicts arise. |
| Topological sort by data dependency | v1 uses explicit declaration order. Add auto-ordering when the manual approach becomes painful. |
| State key runtime enforcement | v1 trusts `reads`/`writes` declarations. The trace log makes violations visible (a node touches state it didn't declare → shows up as unexpected read/write in trace). |
| Thread safety validation | v1 documents which hooks are parallel. Add enforcement when a modder actually writes unsafe code. |

## v1 Implementation Scope

### Framework components

```
BOAM.Core (F#)
├── StateKey.fs         — StateKey<'t> type, lifetime enum
├── NodeContext.fs       — read/write/update API for node functions
├── Node.fs              — node record type, builder syntax
├── Registry.fs          — node collection, merge, validation
├── Walker.fs            — per-hook execution with tracing
├── ScoreDiff.fs         — before/after score snapshots
└── Trace.fs             — trace output formatting

BOAM.Hooks (C#)
├── HarmonyPatches.cs    — patches for each supported hook point
└── BoamPlugin.cs        — IModpackPlugin, registers patches, initializes framework
```

### Supported hooks in v1

| Hook | Timing | Threading | Score type captured |
|------|--------|-----------|-------------------|
| OnTurnStart | Prefix | Single | — |
| OnTurnStart | Postfix | Single | — |
| OnRoundStart | Prefix | Single | — |
| ConsiderZones.Evaluate | Postfix | Parallel (per-tile) | TileScore.UtilityScore |
| Entity.SetTile | Postfix | Single | — |
| OnDamageReceived | Postfix | Single | — |

Behavior hooks (InflictDamage.Evaluate etc.) deferred to v1.1 — they require understanding the behavior evaluation lifecycle better.

### Console commands

```
boam status           — show installed mods, registered nodes, state keys, validation results
boam trace on|off     — toggle execution tracing
boam trace verbose    — include per-tile ConsiderZones detail
boam graph            — dump merged graph as Mermaid to log
boam state            — dump current state store contents
boam diff             — show last agent's before/after score summary
```

## Score Export

When capture is enabled, BOAM dumps one CSV per AI actor per round with all evaluated tile scores. The modder can toggle BOAM on/off, reload the same save, play the same round, and manually compare the two sets of CSVs.

### Output

```
Mods/BOAM/scores/
├── round_01_faction2_Raider_03_tiles.csv
├── round_01_faction2_Raider_03_behaviors.csv
├── round_01_faction2_Raider_05_tiles.csv
├── round_01_faction2_Raider_05_behaviors.csv
├── round_01_faction7_Wolf_01_tiles.csv
├── round_01_faction7_Wolf_01_behaviors.csv
└── ...
```

Two files per actor that had tiles evaluated during that round: tile scores and behavior scores.

### CSV format

Two files per actor: tile scores (where to go) and behavior scores (what to do).

#### Tile scores (`*_tiles.csv`)

```csv
tile_x,tile_z,utility,safety,distance,final
14,7,12.5,8.3,2.1,42.1
13,8,0.0,15.2,1.0,38.0
15,6,0.0,6.1,3.2,21.4
14,8,8.3,3.2,1.4,12.1
12,9,0.0,12.0,4.5,18.3
```

Every tile evaluated by ConsiderZones for that actor, all score components, one row per tile.

#### Behavior scores (`*_behaviors.csv`)

```csv
behavior,target_actor,target_tile_x,target_tile_z,hit_chance,expected_damage,score,selected
InflictDamage,Player_01,16,5,0.72,45.0,85,true
InflictSuppression,Player_01,16,5,0.85,12.0,34,false
Move,,14,7,,,42,false
Stun,,,,,,0,false
```

Every behavior evaluated for that actor. `hit_chance` and `expected_damage` only apply to attack behaviors. `selected` marks the one the AI picked.

### PNG heatmaps

Optionally, render one PNG per actor per round — a grid image where each tile is a colored block.

```
Mods/BOAM/scores/
├── round_01_faction2_Raider_03_final.png
├── round_01_faction2_Raider_05_final.png
└── ...
```

Rendering: map `sizeX × sizeZ`, configurable px-per-tile (default 8). `Color.Lerp(cold, hot, normalizedScore)` per tile. Unevaluated tiles transparent. Same `Texture2D.EncodeToPNG()` technique as TacticalMap. Same grid dimensions — PNGs are compositable with TacticalMap's terrain output in any image editor.

### Usage

```
boam capture on|off     — toggle capture
boam capture png on|off — also generate PNGs (off by default)
```

Off by default. The modder's workflow: load save → play round → look at CSVs/PNGs → toggle BOAM off → reload same save → play round → compare the two sets.

## Success Criteria

v1 is successful if:

1. BooAPeek can be re-expressed as BOAM nodes with no behavioral change
2. The trace log shows exactly why the AI moved toward a ghost position
3. A second mod (UnderFire) can register nodes and the merged graph validates correctly
4. Missing dependency (Potshot without BooAPeek) produces a clear warning at startup
5. The per-tile overhead on ConsiderZones is under 0.1ms total (all BOAM nodes combined)
6. Score CSVs dump all tile scores per actor per round — modder compares two runs (BOAM on vs off) manually
7. PNG heatmaps are compositable with TacticalMap's terrain output
