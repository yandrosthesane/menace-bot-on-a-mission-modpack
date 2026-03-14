# Reset

Full game cycle: quit → deploy → start tactical engine → launch game.
The tactical engine auto-navigates to tactical when the Title scene loads (event-driven).

## Usage

`/reset [ModName]` — e.g., `/reset BOAM` (defaults to BOAM if no arg given)

## Instructions

Run all steps sequentially using **registered skills** — NEVER use raw bash for game management. Do NOT ask the user for confirmation between steps.

### Step 1: Quit game

Use the `/quit-game` skill.

### Step 2: Deploy mod

Use the `/deploy <ModName>` skill. If deploy fails, stop and report the error.

### Step 3: Start tactical engine (only if deploying BOAM)

Use the `/start-tactical-engine` skill.

### Step 4: Launch game

Run using `run_in_background: true`:
```bash
steam -applaunch 2432860
```

That's it. The tactical engine auto-navigates to tactical when the game reaches the Title scene. No need to wait for bridge or load save.

## Output

Report each step's result concisely. Tell the user the game is launching and will auto-navigate to tactical.
