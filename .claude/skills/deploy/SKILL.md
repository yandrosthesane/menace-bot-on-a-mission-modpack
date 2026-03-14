---
name: deploy
description: Deploy a modpack to the game. Copies source to staging, compiles C# via MCP, and installs to the game Mods directory.
allowed-tools: Bash, Read
---

# Deploy Modpack

Deploy a Menace modpack to the game for testing.

## Usage

`/deploy <ModName>` — e.g., `/deploy BooAPeek`

## What it does

1. Builds ModpackLoader
2. Copies source from `/home/yandros/workspace/menace_mods/<ModName>-modpack/` to staging
3. Calls the Modkit MCP `deploy_modpack` tool via JSON-RPC (compiles C# → DLL)
4. Installs compiled mod to the game's `Mods/` directory
5. Runs any mod-specific post-deploy steps (e.g., BOAM icon regeneration)

## Instructions

Each mod has its own deploy script at `/home/yandros/workspace/menace_mods/<ModName>-modpack/deploy.sh`. Run it with the mod name provided as `$ARGUMENTS`:

```bash
/home/yandros/workspace/menace_mods/$ARGUMENTS-modpack/deploy.sh
```

If the deploy succeeds, tell the user to restart the game to test.

If the deploy fails with a compilation error, read the error output and help the user fix the source code.
