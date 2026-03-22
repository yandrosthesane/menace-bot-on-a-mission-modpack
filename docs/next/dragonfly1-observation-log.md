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

===============

Test 2

Battle 2.1 (opponent_filter=false, concealment +2, 110 AP, seed 5) player get vision on R2

Dragonfly.1
- R1-R5: (26,38) idle
- R6: → (32,38)
- R7–R10: idle

Dragonfly.2
- R1: → (31,4)
- R2: → (27,4)
- R3: → (26,6)
- R4: → (27,10)
- R5: → (24,7)
- R6: → (21,9)
- R7: → (25,11)
- R8: → (21,12)
- R9: → (20,14)
- R10: → (22,16)

Dragonfly.3
- R1: → (38,5)
- R2: → (40,2)
- R3: → (36,1)
- R4: → (32,1)
- R5: → (31,3)
- R6: → (35,4)
- R7: → (31,6)
- R8: → (27,5)
- R9: → (30,8)
- R10: → (28,4)


Battle 2.1 (opponent_filter=true, concealment +2, 110 AP, seed 5) player get vision on R2

Dragonfly.1 — (26,38) — near-static, player in concealed LOS from R3
- R1: → (26,38) idle
- R2: idle
- R3: → (25,37) 
- R4–R9: idle
- R10: → (25,32)

Dragonfly.2
- R1: → (33,0)
- R2: → (35,3)
- R3: → (34,6)
- R4: → (37,6)
- R5: → (38,5)
- R6: → (36,6)
- R7: → (40,6)
- R8: → (41,10)
- R9: → (40,6)
- R10: → (37,6)

Dragonfly.3
- R1: → (38,5)
- R2: idle
- R3: → (41,8) → (41,4)
- R4: → (37,4)
- R5: → (34,7) → (30,5)
- R6: → (35,5)
- R7: → (37,1)
- R8: → (39,4)
- R9: → (36,6)

  ---
Battle 2.2 (opponent_filter=true, concealment +2, 110 AP, seed 5) - player stay on spawn

Dragonfly.1 
- R1: → (27,38)
- R2: → (29,40)
- R3: → (25,38)
- R4: → (26,40)
- R5: → (29,38)
- R6: idle
- R7: → (30,38)
- R8: idle

Dragonfly.2 
- R1: → (27,0)
- R2: → (29,4)
- R3: → (33,4)
- R4: → (37,2)
- R5: → (34,3)
- R6: → (30,3)
- R7: → (29,6)

Dragonfly.3 
- R1: → (38,3)
- R2: → (40,4)
- R3: → (37,1)
- R4: → (38,3)
- R5: → (38,7)
- R6: → (33,7)
- R7: → (28,7)

Battle 2.3 (opponent_filter=false, concealment +2, 110 AP, seed 5) - player stay on spawn

Dragonfly.1
- R1: → (27,38)
- R2: → (25,38)
- R3: → (28,40)
- R4: → (32,38)
- R5: → (32,41)
- R6: → (31,41)
- R7: → (34,38)
- R8: → (35,37)
- R9: → (32,35)
- R10: → (29,38)

Dragonfly.2
- R1: → (38,5)
- R2: → (35,6)
- R3: → (38,7)
- R4: → (35,7)
- R5: → (32,5)
- R6: → (35,3)
- R7: → (31,1)
- R8: → (31,4)
- R9: → (29,3)
- R10: → (31,5)

Dragonfly.3
- R1: → (40,3)
- R2: → (36,3)
- R3: → (34,7)
- R4: → (34,11)
- R5: → (30,13)
- R6: → (32,16)
- R7: → (33,19)
- R8: → (37,19)
- R9: → (40,19)
- R10: → (39,18)
- 

As a line 

  Battle 2.1 (filter=OFF, concealment +2, 110 AP, seed 5, player gets vision R2)
  - D1: idle → idle → idle → idle → idle → (32,38) → idle → idle → idle → idle
  - D2: (31,4) → (27,4) → (26,6) → (27,10) → (24,7) → (21,9) → (25,11) → (21,12) → (20,14) → (22,16)
  - D3: (38,5) → (40,2) → (36,1) → (32,1) → (31,3) → (35,4) → (31,6) → (27,5) → (30,8) → (28,4)

  Battle 2.1 filtered (filter=ON, concealment +2, 110 AP, seed 5, player gets vision R2)
  - D1: idle → idle → (25,37) → idle → idle → idle → idle → idle → idle → (25,32)
  - D2: (33,0) → (35,3) → (34,6) → (37,6) → (38,5) → (36,6) → (40,6) → (41,10) → (40,6) → (37,6)
  - D3: (38,5) → idle → (41,8)→(41,4) → (37,4) → (34,7)→(30,5) → (35,5) → (37,1) → (39,4) → (36,6)

  Battle 2.2 (filter=ON, concealment +2, 110 AP, seed 5, player stays on spawn)
  - D1: (27,38) → (29,40) → (25,38) → (26,40) → (29,38) → idle → (30,38) → idle
  - D2: (27,0) → (29,4) → (33,4) → (37,2) → (34,3) → (30,3) → (29,6)
  - D3: (38,3) → (40,4) → (37,1) → (38,3) → (38,7) → (33,7) → (28,7)

  Battle 2.3 (filter=OFF, concealment +2, 110 AP, seed 5, player stays on spawn)
  - D1: (27,38) → (25,38) → (28,40) → (32,38) → (32,41) → (31,41) → (34,38) → (35,37) → (32,35) → (29,38)
  - D2: (38,5) → (35,6) → (38,7) → (35,7) → (32,5) → (35,3) → (31,1) → (31,4) → (29,3) → (31,5)
  - D3: (40,3) → (36,3) → (34,7) → (34,11) → (30,13) → (32,16) → (33,19) → (37,19) → (40,19) → (39,18)

Clearly the AI evaluation is affected by the hidden player unit =_=
