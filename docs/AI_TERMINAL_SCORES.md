# AI Terminal Scores — What BOAM Can Pilot

Every AI decision in Menace flows through a scoring pipeline. This document catalogs every point where a mod can inject, override, or transform a score to influence AI behavior.

"Terminal" means: **the final numeric value that determines what the AI actually does.** Upstream logic computes these numbers; the AI acts on them. If you control the number, you control the behavior.

---

## 1. Tile Scoring (Where to Go)

The AI evaluates every reachable tile and picks the best one. Three component scores combine into `FinalScore`:

```
FinalScore = UtilityScore × RoleData.UtilityScale
           + SafetyScore  × RoleData.SafetyScale
           - DistanceScore × RoleData.DistanceScale
```

Then globally scaled: `FinalScore = pow(FinalScore, AIWeightsTemplate.BehaviorScorePOW)`

### TileScore Fields

| Field | Type | Written By | Purpose |
|-------|------|-----------|---------|
| `UtilityScore` | float | ConsiderZones, zone evaluators | Strategic value — zone bonuses, objective proximity |
| `SafetyScore` | float | CoverAgainstOpponents, ThreatFromOpponents, FleeFromOpponents | Survivability — cover quality, threat exposure |
| `DistanceScore` | float | DistanceToCurrentTile | Penalty for tiles far from current position |
| `FinalScore` | float | PostProcess (combined) | Weighted combination of the above |
| `CoverLevel` | int (0–3) | CoverAgainstOpponents | Cover quality against primary threat (None/Light/Medium/Heavy) |
| `VisibleToOpponents` | bool | CoverAgainstOpponents | Whether tile is in opponent LOS |

**Hook point:** `ConsiderZones.Evaluate` postfix — add/subtract from `UtilityScore` per tile. This is where BooAPeek injects ghost bonuses.

**Thread safety:** Criterion.Evaluate runs in parallel across tiles. Per-tile writes are safe; shared state must be read-only (pre-computed in OnTurnStart).

---

## 2. Criterions (What Feeds Tile Scores)

Each Criterion evaluates every tile and writes to TileScore fields. Criterions are enabled/disabled per unit via RoleData bool flags.

| Criterion | Writes To | RoleData Toggle | Threads |
|-----------|-----------|-----------------|---------|
| **ConsiderZones** | UtilityScore | `.ConsiderZones` | 1 |
| **CoverAgainstOpponents** | SafetyScore, CoverLevel, VisibleToOpponents | `.CoverAgainstOpponents` | 1 |
| **ThreatFromOpponents** | SafetyScore (subtracts) | `.ThreatFromOpponents` | multi |
| **DistanceToCurrentTile** | DistanceScore | `.DistanceToCurrentTile` | 1 |
| **FleeFromOpponents** | SafetyScore (negative = flee) | `.AvoidOpponents` | 1 |
| **ExistingTileEffects** | UtilityScore/SafetyScore | `.ExistingTileEffects` | 1 |
| **ConsiderSurroundings** | (Collect phase only) | `.ConsiderSurroundings` | 1 |
| **WakeUp** | (Collect phase, sleeping units) | Internal | 1 |
| **Roam** | (Collect phase, random) | Internal | 1 |

### CoverAgainstOpponents Constants
```
NO_COVER_AT_ALL_PENALTY = 10.0
NOT_VISIBLE_TO_OPPONENTS_MULT = 0.9
COVER_PENALTIES = [1.0, 0.7, 0.4, 0.1]  // None, Light, Medium, Heavy
```

### ThreatFromOpponents Modifiers (from AIWeightsTemplate)
```
ThreatFromPinnedDownOpponents   @ +0x90  — multiplier when opponent is pinned
ThreatFromSuppressedOpponents   @ +0x94  — multiplier when opponent is suppressed
ThreatFrom2xStunnedOpponents    @ +0x98  — multiplier when opponent is double-stunned
ThreatFromFleeingOpponents      @ +0x9C  — multiplier when opponent is fleeing
ThreatFromOpponentsAlreadyActed @ +0xA0  — multiplier when opponent already acted
```

---

## 3. Behavior Scoring (What to Do)

After tile scoring, each Behavior independently computes a score. The AI picks the highest-scoring behavior.

### Behavior Types and Weights

| Behavior | RoleData Weight Field | Score Formula |
|----------|----------------------|---------------|
| **InflictDamage** | `.InflictDamage` (+0x30) | `hitChance × expectedDamage × threatBonus × killBonus × healthBonus → (value × DamageScoreMult) + DamageBaseScore` |
| **InflictSuppression** | `.InflictSuppression` (+0x34) | Similar to damage, with `SuppressionBaseScore` + `SuppressionScoreMult` |
| **Stun** | `.Stun` (+0x38) | Stun-specific scoring |
| **Move** | `.Move` (+0x2C) | `bestTile.FinalScore × moveWeight` |
| **Deploy** | — | Heavy weapon setup scoring |
| **Assist** | — | Ally help scoring |
| **Buff** | — | Buff application scoring |
| **SupplyAmmo** | — | Ammo resupply scoring |
| **Reload** | — | Weapon reload scoring |
| **TurnArmorTowardsThreat** | — | Armor facing scoring |
| **MovementSkill** | `.Move` | Jetpack/teleport scoring |

### InflictDamage Scoring Detail
```
baseValue = hitChance × expectedDamage
value *= 1.0 + (target.ThreatLevel × AIWeightsTemplate.TargetValueThreatScale)
if (damage ≥ target.HP) value *= 1.5    // kill bonus
value *= 1.0 + (1.0 - healthRatio) × 0.3  // wounded target bonus
finalScore = (int)(value × DamageScoreMult + DamageBaseScore)
```

### AIWeightsTemplate Attack Scoring Fields
```
DamageBaseScore          @ +0x104
DamageScoreMult          @ +0x108
SuppressionBaseScore     @ +0x110
SuppressionScoreMult     @ +0x114
TargetValueDamageScale   @ +0xE4
TargetValueSuppressionScale @ +0xEC
TargetValueThreatScale   @ +0xF4
FriendlyFirePenalty      @ +0x100
```

---

## 4. Agent Selection (Which Unit Acts)

When a faction has multiple agents ready, it picks the best one using a score multiplier:

```
agentScore = max(1, selectedBehavior.Score × GetScoreMultForPickingThisAgent())
```

### Score Multiplier Components
```
mult *= 1.2  if actor hasn't acted this turn
mult *= 0.8 + (apRatio × 0.4)  based on remaining AP
```

**Hook point:** `Agent.GetScoreMultForPickingThisAgent` — runs in parallel, read-only access to shared state only.

---

## 5. RoleData (Per-Unit AI Personality)

RoleData is the highest-level control over AI priorities. Each field directly scales the scoring pipeline.

### Scale Weights (float, 0–50)
| Field | Offset | Effect |
|-------|--------|--------|
| `UtilityScale` | +0x14 | Multiplier on UtilityScore → more aggressive |
| `UtilityThresholdScale` | +0x18 | Minimum usefulness threshold |
| `SafetyScale` | +0x1C | Multiplier on SafetyScore → more defensive |
| `DistanceScale` | +0x20 | Multiplier on DistanceScore → stays local |
| `FriendlyFirePenalty` | +0x24 | Avoids shooting through allies |
| `TargetFriendlyFireValueMult` | +0x10 | How much other units protect this one |

### Behavioral Flags
| Field | Offset | Effect |
|-------|--------|--------|
| `IsAllowedToEvadeEnemies` | +0x28 | Can retreat |
| `AttemptToStayOutOfSight` | +0x29 | Prefers fog of war |
| `PeekInAndOutOfCover` | +0x2A | Leaves cover to attack, returns |
| `UseAoeAgainstSingleTargets` | +0x2B | AoE on lone targets |

### Role Archetypes (Presets)
| Archetype | UtilityScale | SafetyScale | Key Traits |
|-----------|-------------|-------------|------------|
| Assault | 30 | 10 | PeekInAndOutOfCover, high InflictDamage |
| Support | 15 | 15 | High InflictSuppression |
| Sniper | 25 | 25 | AttemptToStayOutOfSight, high DistanceScale |
| Tank | 10 | 5 | High FriendlyFirePenalty |
| Civilian | 0 | 50 | AvoidOpponents, InflictDamage=0 |

---

## 6. Opponent Tracking (Who the AI Knows About)

### Opponent Fields
| Field | Type | Purpose |
|-------|------|---------|
| `Actor` | Actor | Live reference to enemy unit |
| `TTL` | int | -2 = pre-populated (never seen), ≥0 = known |
| `Data` | Assessment | Full threat assessment |

### Assessment Fields (per Opponent)
| Field | Type | Purpose |
|-------|------|---------|
| `CurrentThreatPosedTotal` | float | Aggregate threat score |
| `CurrentThreatPosedMax` | float | Maximum single-actor threat |
| `CurrentThreatPosed` | Dict<Actor, float> | Per-actor threat breakdown |
| `CurrentScoreAgainstTotal` | float[] | Multi-category score totals |
| `CurrentScoreAgainstMax` | float[] | Multi-category score maxima |
| `PriorityMult` | float | Priority multiplier |
| `DamageRange` | Range | Effective damage range |
| `SuppressionRange` | Range | Effective suppression range |
| `IsInsideDefendZone` | bool | In defend zone |
| `IsInsideAttackZone` | bool | In attack zone |

**Hook point:** `AIFaction.OnTurnStart` prefix — filter `m_Opponents` list to control what the AI knows. This is where BooAPeek strips unseen opponents.

**Known bug:** The tile scorer reads `Opponent.Actor.GetPosition()` without checking `IsKnown()`. AI has perfect position knowledge of all opponents, including never-sighted ones.

---

## 7. Morale & Suppression (Behavioral State)

Morale indirectly controls AI by influencing behavior selection and enabling/disabling actions.

### Morale Thresholds
| State | Condition | AI Effect |
|-------|-----------|-----------|
| Panicked | ratio ≤ 0 | FleeBehavior +1000, may switch faction |
| Shaken | ratio ≤ 0.5 | Defensive posture, reduced attack scores |
| Steady | ratio > 0.5 | Normal behavior |

### Morale Fields (Actor)
| Field | Offset | Purpose |
|-------|--------|---------|
| `m_Morale` | +0x160 | Current morale value |
| `m_Suppression` | +0x15C | Current suppression |
| `m_CachedMoraleState` | +0xD4 | Cached MoraleState enum |

### UnitStats Morale Multipliers
| Field | Offset | Purpose |
|-------|--------|---------|
| `baseMorale` | +0xA0 | Base max morale |
| `bonusMorale` | +0xAC | Additional morale from buffs |
| `moraleMultiplier` | +0xB0 | Max morale multiplier |
| `moraleDamageMultiplier` | +0xBC | Incoming morale damage multiplier |
| `outgoingMoraleMultiplier` | +0xB8 | Outgoing morale damage multiplier |
| `moraleStateModifier` | +0xC0 | Shifts morale state thresholds |
| `immuneToPanic` | +0xEC bit 7 | Immune to morale damage |

**Hook points:** `Actor.ApplyMorale`, `Actor.SetMorale`, `TacticalManager.OnMoraleStateChanged`

---

## 8. AIWeightsTemplate (Global Tuning)

Singleton ScriptableObject at `AIWeightsTemplate.Instance`. All AI factions share these values.

### Score Processing
| Field | Offset | Purpose |
|-------|--------|---------|
| `BehaviorScorePOW` | +0x18 | Exponent applied to final tile scores |
| `TTL_MAX` | +0x1C | Max evaluation iterations |

### Utility Scaling Pipeline
| Field | Offset | Purpose |
|-------|--------|---------|
| `UtilityPOW` | +0x20 | Pre-scale exponent |
| `UtilityScale` | +0x24 | Pre-scale multiplier |
| `UtilityPostPOW` | +0x28 | Post-scale exponent |
| `UtilityPostScale` | +0x2C | Post-scale multiplier |
| `SafetyPOW` | +0x30 | Safety exponent |
| `SafetyScale` | +0x34 | Safety multiplier |

### Threat Assessment
| Field | Offset | Purpose |
|-------|--------|---------|
| `ThreatFromOpponents` | +0x74 | Base threat weight |
| `ThreatFromOpponentsDamage` | +0x80 | Damage threat weight |
| `ThreatFromOpponentsArmorDamage` | +0x84 | Armor damage threat weight |
| `ThreatFromOpponentsSuppression` | +0x88 | Suppression threat weight |
| `ThreatFromOpponentsStun` | +0x8C | Stun threat weight |

---

## 9. SDK Write Methods (Safe API)

The modkit SDK provides thread-safe write methods for some of these values:

```csharp
AI.SetRoleDataFloat(actor, "UtilityScale", 40f)    // Change role weights
AI.SetRoleDataBool(actor, "PeekInAndOutOfCover", true)
AI.ApplyRoleData(actor, newRoleDataInfo)             // Replace entire role
AI.SetBehaviorScore(actor, "InflictDamage", 9999)    // Force behavior selection
```

**Only safe when `AI.IsAnyFactionThinking() == false`** or during OnTurnStart/OnTurnEnd hooks.

---

## Summary: Terminal Score Hierarchy

```
Level 0 — Global Config
  └── AIWeightsTemplate.Instance (BehaviorScorePOW, UtilityPOW, threat weights...)
        affects ALL factions, ALL units

Level 1 — Per-Unit Personality
  └── RoleData (UtilityScale, SafetyScale, behavior weights, criterion toggles)
        determines HOW scores are weighted for this unit

Level 2 — Per-Tile Evaluation
  └── Criterion.Evaluate() → TileScore fields (UtilityScore, SafetyScore, DistanceScore)
        determines WHERE this unit wants to be
  └── PostProcess → FinalScore = weighted combination
        the actual tile ranking

Level 3 — Per-Behavior Evaluation
  └── Behavior.Evaluate() → Behavior.Score
        determines WHAT this unit wants to do (attack, move, suppress, stun...)

Level 4 — Agent Selection
  └── Agent.GetScoreMultForPickingThisAgent() × Behavior.Score
        determines WHICH unit in the faction acts next

Level 5 — Opponent Knowledge
  └── AIFaction.m_Opponents list (filter/inject to control what AI knows)
        determines WHO the AI considers as threats

Level 6 — Behavioral State
  └── Actor.m_Morale, Actor.m_Suppression
        emergency overrides — panicked units flee regardless of scores
```

### Practical Hook Points for BOAM

| What You Want | Hook | What to Modify |
|---------------|------|----------------|
| AI attracted to a tile | ConsiderZones.Evaluate postfix | `TileScore.UtilityScore += bonus` |
| AI avoids a tile | ConsiderZones.Evaluate postfix | `TileScore.UtilityScore -= penalty` |
| AI more aggressive | OnTurnStart prefix | `RoleData.UtilityScale ↑` |
| AI more defensive | OnTurnStart prefix | `RoleData.SafetyScale ↑` |
| AI ignores a unit | OnTurnStart prefix | Remove from `m_Opponents` |
| AI remembers a unit | OnTurnStart prefix | Add ghost to `m_Opponents` |
| AI prefers attacking | OnTurnStart prefix | `RoleData.InflictDamage ↑` |
| AI prefers suppressing | OnTurnStart prefix | `RoleData.InflictSuppression ↑` |
| AI flees | SetMorale | Set morale to 0 |
| AI never flees | SetMorale | Set morale to max |
| AI targets specific enemy | Behavior.Evaluate postfix | Boost score for desired target |
| Unit acts first in faction | GetScoreMultForPickingThisAgent postfix | Return higher multiplier |
| Change global aggression | Runtime | Modify `AIWeightsTemplate.Instance` fields |
