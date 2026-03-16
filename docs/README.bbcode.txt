[size=5][b]BOAM — Bot On A Mission[/b][/size]

[b]Version:[/b] 1.1.0
[b]Author:[/b] YandrosTheSane

AI behavior analysis and replay mod for Menace.
Intercepts AI decision-making at runtime,
captures tactical data for offline heatmap rendering,
provides a real-time in-game minimap overlay,
records full battle sessions (player actions + AI decisions) for replay with divergence detection.

[size=4][b]Features[/b][/size]

[list]
[*][b]Tactical Minimap[/b] — In-game IMGUI overlay showing unit positions on the captured map background. Toggle with M key, cycle presets with L. [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_MINIMAP.md]Details[/url]
[*][b]Heatmap Renderer[/b] — Offline heatmap generation from deferred render jobs — tile scores, AI decisions, movement destinations. Render via CLI or HTTP API. [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_HEATMAPS.md]Details[/url]
[*][b]Replay System[/b] — Record and replay full battles. Player actions replayed exactly, AI decisions compared via determinism watchdog (halt or log on divergence). [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_REPLAY.md]Details[/url]
[*][b]Configuration[/b] — Versioned JSON5 configs with user/mod-default two-tier system. User configs survive mod updates. [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_CONFIG.md]Details[/url]
[/list]

[size=4][b]Components[/b][/size]

[list]
[*][b]C# Bridge Plugin[/b] (in-game, MelonLoader/Wine) — Harmony patches, map capture, minimap overlay, action forwarding. [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_BRIDGE_PLUGIN.md]Details[/url]
[*][b]F# Tactical Engine[/b] (native .NET 10, port 7660) — Render jobs, heatmap renderer, action logger, replay engine, determinism watchdog. [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_TACTICAL_ENGINE.md]Details[/url]
[*][b]Icon Generator[/b] (CLI tool) — Resizes game badge art into heatmap/minimap icons. [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_ICON_GENERATOR.md]Details[/url]
[/list]

[size=4][b]Feature Toggles[/b][/size]

By default, only the tactical minimap is enabled. All other features are opt-in via config:

[list]
[*][b]Minimap[/b] — Enabled by default. Toggle in [i]tactical_map.json5[/i] ("Enabled": true/false)
[*][b]Action Logging[/b] — [i]config.json5[/i]: "action_logging": true — Logs player actions + AI decisions. Required for replay.
[*][b]AI Logging[/b] — [i]config.json5[/i]: "ai_logging": true — Adds AI decisions to logs. Enables determinism watchdog.
[*][b]Heatmaps[/b] — [i]config.json5[/i]: "heatmaps": true — Collects tile score data for offline heatmap rendering.
[/list]

[size=4][b]Usage[/b][/size]

[size=3][b]Start the Engine[/b][/size]

[b]Linux:[/b]
[code]
# Passive — engine starts, you control everything
./start-tactical-engine.sh

# Auto-navigate to tactical when game connects
./start-tactical-engine.sh --on-title /navigate/tactical

# Auto-navigate + start a replay
./start-tactical-engine.sh --on-title /navigate/replay/battle_2026_03_15_15_14

# Replay with determinism watchdog — halt on first AI divergence
./start-tactical-engine.sh --on-title "/navigate/replay/battle_2026_03_15_15_14?determinism=stop"
[/code]

[b]Windows:[/b]
[code]
REM Passive
start-tactical-engine.bat

REM Auto-navigate to tactical
start-tactical-engine.bat --on-title /navigate/tactical

REM Auto-navigate + replay
start-tactical-engine.bat --on-title /navigate/replay/battle_2026_03_15_15_14

REM Replay with determinism watchdog
start-tactical-engine.bat --on-title "/navigate/replay/battle_2026_03_15_15_14?determinism=stop"
[/code]

Then launch the game normally through Steam. On Linux the engine opens in its own terminal window; on Windows it runs in the command prompt.

[size=3][b]In-Game Minimap[/b][/size]

[list]
[*][b]M[/b] — Toggle minimap on/off
[*][b]L[/b] — Cycle display presets (size/anchor)
[/list]

[size=3][b]Render Heatmaps[/b][/size]

[b]Linux:[/b]
[code]
# CLI — render and exit
./TacticalEngine --render battle_2026_03_15_15_14
./TacticalEngine --render battle_2026_03_15_15_14 --pattern "r01_*"
[/code]

[b]Windows:[/b]
[code]
REM CLI — render and exit
tactical_engine\TacticalEngine.exe --render battle_2026_03_15_15_14
tactical_engine\TacticalEngine.exe --render battle_2026_03_15_15_14 --pattern "r01_*"
[/code]

[b]HTTP (any platform, while engine is running):[/b]
[code]
curl -s -X POST http://127.0.0.1:7660/render/battle/battle_2026_03_15_15_14 -d '{}'
[/code]

[size=3][b]Replay a Battle[/b][/size]

[b]Linux:[/b]
[code]
# Fully automated: navigate + replay
./start-tactical-engine.sh --on-title /navigate/replay/battle_2026_03_15_15_14

# With determinism watchdog
./start-tactical-engine.sh --on-title "/navigate/replay/battle_2026_03_15_15_14?determinism=stop"
[/code]

[b]Windows:[/b]
[code]
REM Fully automated: navigate + replay
start-tactical-engine.bat --on-title /navigate/replay/battle_2026_03_15_15_14

REM With determinism watchdog
start-tactical-engine.bat --on-title "/navigate/replay/battle_2026_03_15_15_14?determinism=stop"
[/code]

[b]HTTP (any platform):[/b]
[code]
curl -s http://127.0.0.1:7660/replay/battles
curl -s -X POST http://127.0.0.1:7660/replay/start -d '{"battle":"battle_2026_03_15_15_14","determinism":"stop"}'
curl -s http://127.0.0.1:7660/replay/divergences
[/code]

The determinism watchdog compares AI decisions during replay against the original recording. Divergences report which actor made a different decision, what was expected vs actual, and the last player action before the divergence.

[size=4][b]Changelog[/b][/size]

[size=3][b]v1.1.0[/b][/size]
[list]
[*][b]Determinism watchdog[/b] — Compare AI decisions during replay against original recording. Two modes: halt on first divergence, or log all divergences.
[*][b]Select fix[/b] — Actor selection during replay now simulates portrait click (OnLeftClicked) instead of cycling through the turn bar, eliminating RNG pollution.
[*][b]Feature gates[/b] — Heatmaps, action logging, and AI logging are now opt-in via config. Only the minimap is enabled by default.
[*][b]Production launcher[/b] — Engine launcher opens a dedicated terminal window with live output and logs to file. Dev script removed.
[*][b]Bridge halt support[/b] — Bridge correctly stops replay on "halted" status from determinism watchdog.
[*][b]Install doc improvements[/b] — Corrected directory layout, added shell shortcut guide.
[/list]

[size=3][b]v1.0.0[/b][/size]
Initial release: C# bridge plugin, F# tactical engine, tactical minimap, heatmap renderer, replay system, icon generator.

[size=4][b]Installation[/b][/size]

Full guide: [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_INSTALL.md]Installation Guide[/url]

[list=1]
[*]Install MelonLoader + [url=https://github.com/YandrosTheSane/menace-modpack-loader]Menace ModpackLoader[/url]
[*]Deploy the C# bridge via Modkit
[*]Extract the tactical engine archive into Mods/BOAM/
[*]Extract game art and run the icon generator
[*](Optional) Set up shell shortcuts for convenience
[*](Optional) Customise configs in UserData/BOAM/configs/
[/list]

[size=4][b]Documentation[/b][/size]

[list]
[*][url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_INSTALL.md]Installation Guide[/url]
[*][url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_MINIMAP.md]Tactical Minimap[/url]
[*][url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_HEATMAPS.md]Heatmap Renderer[/url]
[*][url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_REPLAY.md]Replay Manual[/url]
[*][url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_CONFIG.md]Configuration[/url]
[*][url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_BRIDGE_PLUGIN.md]Bridge Plugin[/url]
[*][url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_TACTICAL_ENGINE.md]Tactical Engine[/url]
[*][url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_ICON_GENERATOR.md]Icon Generator[/url]
[/list]

[size=4][b]Requirements[/b][/size]

[list]
[*]Menace with MelonLoader
[*]Menace ModpackLoader
[*].NET 10 runtime (bundled with tactical engine)
[/list]

Source code: [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack]GitHub[/url]
