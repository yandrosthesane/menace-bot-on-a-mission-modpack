# Dragonfly.1 Observation Log

Raw data from battle `battle_2026_03_22_01_44`. Wildlife faction (7) vs single player unit.
Game seed: **5**. No mods affecting AI decisions — BOAM is read-only (observation + logging only).

## Round 1

- **Player** ends turn at (23,29)
- **Dragonfly.1** moves (28,40) → (30,37)
- **Decision**: Move(49) target=(30,37)
- **Alternatives**: Move(49), Idle(1), InflictDamage(0)
- **Criterion scores**: 66 tiles, 0 visible, 0 with safety
- **Top tiles**: (28,40)=0, (27,39)=-25, (27,40)=-25

## Round 2

- **Player** ends turn at (23,29)
- **Dragonfly.1** moves (30,37) → (32,33)
- **Decision**: Move(49) target=(32,33)
- **Alternatives**: Move(49), Idle(1), InflictDamage(0)
- **Criterion scores**: 95 tiles, 0 visible, 0 with safety
- **Top tiles**: (32,33)=50, (30,37)=0

## Round 3

- **Player** ends turn at (23,29)
- **Dragonfly.1** stays at (32,33)
- **Decision**: Idle(1)
- **Alternatives**: Idle(1), Move(0), InflictDamage(0)
- **Criterion scores**: 101 tiles, 0 visible, 0 with safety
- **Top tiles**: (32,33)=0, (31,33)=-25

## Round 4

- **Player** ends turn at (23,29)
- **Dragonfly.1** stays at (32,33)
- **Decision**: Idle(1)
- **Alternatives**: Idle(1), Move(0), InflictDamage(0)
- **Criterion scores**: 100 tiles, 0 visible, 0 with safety
- **Top tiles**: (32,33)=0, (31,33)=-25

## Round 5

- **Player** ends turn at (23,29)
- **Dragonfly.1** moves (32,33) → (29,30)
- **Decision**: Move(49) target=(29,30), evaluated 48 times
- **Alternatives**: Move(49), Idle(1), InflictDamage(0)
- **Criterion scores**: 100 tiles, 0 visible, 0 with safety
- **Top tiles**: (29,30)=50, (32,33)=0

## Round 6

- **Player** moves (23,29) → (22,29)
- **Dragonfly.1** moves (29,30) → (26,31)
- **Decision**: Move(49) target=(26,31), evaluated 42 times
- **Alternatives**: Move(49), Idle(1), InflictDamage(0)
- **Criterion scores**: 100 tiles, 0 visible, 0 with safety
- **Top tiles**: (26,31)=75, (29,30)=0

## Round 7

- **Player** moves (22,29) → (19,29)
- **Dragonfly.1** moves (26,31) → (25,32), fires Shoot at (19,29), moves → (26,31)
- **Decision**: Move(19113) target=(25,32), evaluated 69 times
- **Alternatives**: Move(19113), Idle(1), InflictDamage(0)
- **Criterion scores**: 129 tiles, **87 visible**, **87 with safety**
- **Top tiles**: (27,30)=-31, (27,31)=-31, (27,32)=-31, (26,31)=-33
- **Worst safety**: (21,32) safety=-9451 utility=185, (22,27) safety=-9451 utility=185
- **Note**: InflictDamage scored 0 in alternatives despite firing Shoot. Move score jumped to 19113.

## Round 8

- **Player** moves (19,29) → (19,24)
- **Dragonfly.1** moves (26,31) → (23,29)
- **Decision**: Move(49) target=(23,29), evaluated 44 times
- **Alternatives**: Move(49), Idle(1), InflictDamage(0)
- **Criterion scores**: 144 tiles, **75 visible**, **75 with safety**
- **Top tiles**: (26,31)=0, (25,31)=-25, (25,32)=-25
- **Worst safety**: (17,27) safety=-7060 utility=178, (18,27) safety=-7060 utility=178
- **Note**: Player moved to (19,24) but dragonfly moved to (23,29) — the player's round 7 position. The `isVisible` flag and safety scores persisted from round 7. The move target (23,29) does not appear in the top-scored tiles; tile (26,31) scored highest at 0.

## Player Observations (in-game)

- All units moved EXCEPT the one in the player's LOS (round 2, first battle).
- The dragonfly came into LOS but didn't fire despite being in range (rounds 4-5).
- To confirm: after firing and moving out of LOS, the AI still tracked the player's previous position (round 8).
- To confirm: the AI moved to the player's old position, not current position, after the player relocated (round 8).

## Second Battle (with concealment patch: +2 concealment, 150 AP)

- Dragonfly froze (Idle) for rounds 3 and 4 while a concealed player unit was observing it.
- As soon as another unit spotted the player, the frozen enemies started moving again.
- This confirms: enemies freeze when observed by a concealed unit they cannot detect.

## Next Test

Re-implement the BAP opponent filter in the `OnTurnStart` prefix — strip opponents from `m_Opponents` that the AI faction hasn't spotted via LOS. Deploy with the same seed (5) and concealment patch (+2 concealment, 150 AP). Check if the dragonfly still freezes when it can't detect the concealed player, or if it roams normally now that the unseen player is removed from its opponent list.

Hypothesis: the freeze happens because the AI knows the player exists (via `m_Opponents`) but can't resolve it in its scoring. Removing the unseen player from the list should let the AI behave normally.

## Raw Data Observations

- Rounds 1–6: dragonfly.1 has 0 visible tiles, 0 safety scores. No knowledge of the player.
- Round 7: 87 visible tiles, safety scores around -9400. Dragonfly fires and moves.
- Round 8: 75 visible tiles persist. Safety scores still present (~-7000). Knowledge carried over from round 7.
- Round 8 move target (23,29) does not match the highest criterion-scored tile (26,31)=0. The Move behavior target is not explained by the tile criterion scores captured in `PostProcessTileScores`.
- The `InflictDamage` alternative scored 0 in every round, including round 7 where the dragonfly fired.
- The Move decision was re-evaluated 40-70 times in rounds 5-8 (looping pattern).
