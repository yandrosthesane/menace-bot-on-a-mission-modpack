[size=5][b]BOAM — Bot On A Mission[/b][/size]

[b]Version:[/b] 1.1.0
[b]Author:[/b] YandrosTheSane

AI behavior analysis and replay mod for Menace.
Intercepts AI decision-making at runtime, captures tactical data for offline heatmap rendering, provides a real-time in-game minimap overlay, and records full battle sessions for replay with divergence detection.

[size=4][b]Features[/b][/size]

[list]
[*][b]Tactical Minimap[/b] — In-game overlay showing unit positions on the captured map. Toggle with [b]M[/b], cycle presets with [b]L[/b]. [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_MINIMAP.md]Details[/url]
[*][b]Heatmap Renderer[/b] — Offline heatmap generation from AI tile scores, decisions, and movement. [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_HEATMAPS.md]Details[/url]
[*][b]Battle Replay[/b] — Record and replay full battles with a determinism watchdog that detects AI decision divergence. [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_REPLAY.md]Details[/url]
[*][b]Configurable[/b] — JSON5 configs with user overrides that survive mod updates. [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_CONFIG.md]Details[/url]
[/list]

By default only the minimap is enabled. Heatmaps, action logging, and replay are opt-in via [i]config.json5[/i].

[size=4][b]Quick Start[/b][/size]

[list=1]
[*]Install MelonLoader + [url=https://github.com/YandrosTheSane/menace-modpack-loader]Menace ModpackLoader[/url]
[*]Deploy the C# bridge via Modkit
[*]Extract the tactical engine archive into [i]Mods/BOAM/[/i]
[*]Extract game art and run the icon generator
[/list]

[b]Start the engine, then launch the game:[/b]
[code]
# Linux
./start-tactical-engine.sh --on-title /navigate/tactical

# Windows
start-tactical-engine.bat --on-title /navigate/tactical
[/code]

Full setup: [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_INSTALL.md]Installation Guide[/url]
Full usage (heatmaps, replay, HTTP API): [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README.md]README on GitHub[/url]

[size=4][b]Changelog[/b][/size]

[size=3][b]v1.1.0[/b][/size]
[list]
[*][b]Determinism watchdog[/b] — Compare AI decisions during replay against the original recording. Halt or log on divergence.
[*][b]Select fix[/b] — Actor selection during replay simulates portrait click instead of cycling, eliminating RNG pollution.
[*][b]Feature gates[/b] — Heatmaps, action logging, AI logging are opt-in. Only minimap enabled by default.
[*][b]Production launcher[/b] — Engine opens a dedicated terminal with live output and log file.
[/list]

[size=3][b]v1.0.0[/b][/size]
Initial release: C# bridge, F# tactical engine, minimap, heatmap renderer, replay system, icon generator.

[size=4][b]Virus Scan Notice[/b][/size]

The tactical engine archive bundles a self-contained .NET 10 runtime (~80MB). Some virus scanners may flag bundled .NET system DLLs (e.g. [i]System.Private.CoreLib.dll[/i], [i]clrjit.dll[/i]) as suspicious — these are standard Microsoft runtime files, not malware. The source code is fully available on [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack]GitHub[/url].

[size=4][b]Related Mods[/b][/size]

[list]
[*][url=https://www.nexusmods.com/menace/mods/86]Tactical Map ~ By YandrosTheSane[/url] — Standalone minimap mod. The tactical minimap feature in BOAM supersedes this mod. Tactical Map will not be updated further.
[*][url=https://www.nexusmods.com/menace/mods/73]PeekABoo ~ By YandrosTheSane[/url] — AI awareness replay tool. Will be reimplemented within BOAM's replay and analysis framework.
[/list]

[size=4][b]Requirements[/b][/size]

[list]
[*]Menace with MelonLoader
[*]Menace ModpackLoader
[*].NET 10 runtime (bundled with tactical engine)
[/list]

[size=4][b]Documentation[/b][/size]

[list]
[*][url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_INSTALL.md]Installation Guide[/url]
[*][url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_MINIMAP.md]Tactical Minimap[/url]
[*][url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_HEATMAPS.md]Heatmap Renderer[/url]
[*][url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_REPLAY.md]Replay Manual[/url]
[*][url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_CONFIG.md]Configuration[/url]
[*][url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack/blob/main/README_TACTICAL_ENGINE.md]Tactical Engine (HTTP API)[/url]
[/list]

Source code: [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack]GitHub[/url]
