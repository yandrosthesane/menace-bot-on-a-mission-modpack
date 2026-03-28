# BooAPeek decomposed into independent units

Breaking down BooAPeek's logic into self-contained units, classified by what they *are* rather than where they live in the code today.

---

## State

Persistent data that lives across frames/turns/rounds.

| ID | What | Scope | Lifetime |
|----|------|-------|----------|
| **S1** | `FactionAwareness.LastSeen` — map of (actor → last known x,z) per faction | Per faction | Tactical session |
| **S2** | `FactionAwareness.Ghosts` — map of (actor → GhostMemory) per faction | Per faction | Tactical session |
| **S3** | `GhostsUpdatedThisRound` flag | Per faction | Reset each round |
| **S4** | Faction classification (hostile AI / allied AI / player) | Global | Tactical session |
| **S5** | Calibration spread (`_observedMax`, `_observedMin`, `_calibratedSpread`) | Per faction | Rolling, snapshots each round |

---

## Queries

Pure reads — given state and game data, compute a value. No side effects.

| ID | What | Inputs | Output |
|----|------|--------|--------|
| **Q1** | Can any enemy in faction see this actor? | faction index, actor | bool |
| **Q2** | Ghost score bonus for a tile | faction index, tile x/z, ghost zone size | float |
| **Q3** | Calibrated ghost bonus (spread-scaled) | faction index, tile x/z, spread, settings | float |
| **Q4** | Living actors for a faction | faction index | actor list |
| **Q5** | Nearest AI actor to a position | faction index, x/z | actor, distance |

---

## Commands

State mutations — change persistent state in response to an event.

| ID | What | Trigger | State modified |
|----|------|---------|----------------|
| **C1** | Record sighting — update LastSeen, cancel active ghost | Actor becomes visible to faction | S1, S2 |
| **C2** | Record LOS lost — create ghost from LastSeen | Actor was visible, now isn't | S1, S2 |
| **C3** | Decay ghost priority | Per turn (once per round) | S2 |
| **C4** | Expire ghost | Priority ≤ 0 or rounds exhausted | S2 |
| **C5** | Compute ghost waypoint | Per turn (once per round) | S2 |
| **C6** | Snapshot calibration spread | Round start | S5 |
| **C7** | Reset round flags | Round start | S3 |
| **C8** | Track tile score (update min/max) | Per tile evaluation | S5 |
| **C9** | Discover and classify factions | 60 frames after tactical scene load | S4 |

---

## Filters

Intercept game data, transform it before the AI sees it.

| ID | What | Hook point | Effect |
|----|------|-----------|--------|
| **F1** | Strip unseen opponents from `m_Opponents` | `OnTurnStart` prefix | AI only considers opponents it has LOS to |

This is the core fix — everything else supports or enhances it.

---

## Injections

Add data into the AI scoring pipeline.

| ID | What | Hook point | Effect |
|----|------|-----------|--------|
| **I1** | Add ghost utility bonus to tile score | `ConsiderZones.Evaluate` postfix | AI is attracted toward ghost waypoints |

---

## Observers

Watch game events, feed data into Commands. No game state modified, only BOAM state.

| ID | What | Hook point | Produces |
|----|------|-----------|----------|
| **O1** | Detect mid-move sighting | `Entity.SetTile` postfix | C1 (record sighting) |
| **O2** | Detect LOS changes during opponent filtering | `OnTurnStart` prefix (inside F1) | C1 or C2 |
| **O3** | Track tile utility scores for calibration | `ConsiderZones.Evaluate` postfix | C8 |

---

## Lifecycle / Setup

One-time or scene-transition logic.

| ID | What | When |
|----|------|------|
| **L1** | Register settings | `OnInitialize` |
| **L2** | Apply Harmony patches | `OnInitialize` |
| **L3** | Discover factions (delayed) | 60 frames after tactical scene load |
| **L4** | Reset state on scene exit | Scene transition away from tactical |

---

## Dependency graph

```
L3 (faction discovery)
 └──► S4 (faction classification)
       │
       ├──► O1 (mid-move sighting observer)
       │     └──► C1 (record sighting)
       │           └──► S1 (LastSeen)
       │
       ├──► F1 (opponent filter) ← OnTurnStart prefix
       │     ├──► Q1 (can faction see actor?)
       │     ├──► C1 (record sighting) ──► S1
       │     └──► C2 (record LOS lost) ──► S2 (ghosts)
       │
       ├──► C3/C4/C5 (ghost maintenance) ← OnTurnStart postfix
       │     ├──► Q5 (nearest AI actor)
       │     └──► S2 (ghosts)
       │
       ├──► C6/C7 (round reset) ← OnRoundStart prefix
       │     ├──► S3 (round flags)
       │     └──► S5 (calibration)
       │
       ├──► O3 (tile score tracking) ← ConsiderZones postfix
       │     └──► C8 ──► S5 (calibration)
       │
       └──► I1 (ghost bonus injection) ← ConsiderZones postfix
             ├──► Q3 (calibrated bonus)
             └──► S2, S5
```

## Observations

1. **F1 is the only Filter** — it's the actual fix. Everything else is infrastructure to make it work well.
2. **I1 is the only Injection** — ghost pursuit. Without it, the filter alone would make AI passive (it forgets what it can't see).
3. **O1/O2/O3 are all Observers** — they bridge game events to state updates. They don't modify game data.
4. **C1-C9 are all Commands** — pure state mutations, testable independently.
5. **Q1-Q5 are all Queries** — pure functions, no state changes.
6. **The hook points are few**: `OnTurnStart`, `OnRoundStart`, `Entity.SetTile`, `ConsiderZones.Evaluate`. BOAM needs to own patches on just these four functions and dispatch to registered handlers.
