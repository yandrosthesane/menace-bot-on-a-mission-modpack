[size=5][b]BOAM — Bot On A Mission[/b][/size]

[b]Version:[/b] 1.2.0
[b]Author:[/b] YandrosTheSane

AI behavior analysis mod for Menace.
Intercepts AI decision-making at runtime, captures tactical data for offline heatmap rendering, provides a real-time in-game minimap overlay, and records full battle sessions (player actions + AI decisions + combat outcomes).

[size=4][b]Features[/b][/size]

[list]
[*][b]Tactical Minimap[/b] — In-game overlay showing unit positions on the captured map. Toggle with [b]M[/b], cycle presets with [b]L[/b]. [url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_MINIMAP]Details[/url]
[*][b]Heatmap Renderer[/b] — Offline heatmap generation from AI tile scores, decisions, and movement. [url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_HEATMAPS]Details[/url]
[*][b]Action Logging[/b] — Records player actions, AI decisions, and combat outcomes to JSONL battle logs for post-battle analysis.
[*][b]Configurable[/b] — JSON5 configs with user overrides that survive mod updates. [url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_CONFIG]Details[/url]
[/list]

By default only the minimap is enabled. Heatmaps and action logging are opt-in via [i]config.json5[/i].
The minimap works standalone — no tactical engine needed.

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

Full setup: [url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_INSTALL]Installation Guide[/url]
Full documentation: [url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack]BOAM Documentation[/url]

[size=4][b]Changelog[/b][/size]

[size=3][b]v1.2.0[/b][/size]
[list]
[*][b]Standalone minimap[/b] — Minimap works without the tactical engine. Only start the engine for heatmaps or action logging.
[*][b]Bounded context architecture[/b] — Both bridge and engine reorganized into independent bounded contexts with own types.
[*][b]Config auto-seeding[/b] — Configs automatically copied to UserData on first run. No manual setup needed.
[*][b]Engine startup banner[/b] — Shows config source and active/inactive features at launch.
[*][b]Replay system removed[/b] — Experimental replay removed. Action logging and AI capture remain.
[/list]

Full changelog: [url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/CHANGELOG]Changelog[/url]

[size=4][b]Download Variants[/b][/size]

Two tactical engine variants are available:
[list]
[*][b]Bundled[/b] (~112 MB) — includes .NET 10 runtime, no setup needed. Some virus scanners may flag bundled .NET system DLLs (e.g. [i]System.Private.CoreLib.dll[/i], [i]clrjit.dll[/i]) — these are standard Microsoft runtime files, not malware.
[*][b]Slim[/b] (~5 MB) — requires [url=https://dotnet.microsoft.com/download/dotnet/10.0].NET 10 runtime[/url] installed on your system. No false positive issues.
[/list]

Source code: [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack]GitHub[/url].

[size=4][b]Related Mods[/b][/size]

[list]
[*][url=https://www.nexusmods.com/menace/mods/86]Tactical Map ~ By YandrosTheSane[/url] — Standalone minimap mod. The tactical minimap feature in BOAM supersedes this mod. Tactical Map will not be updated further.
[*][url=https://www.nexusmods.com/menace/mods/73]PeekABoo ~ By YandrosTheSane[/url] — AI awareness tool. Will be reimplemented within BOAM's analysis framework.
[/list]

[size=4][b]Requirements[/b][/size]

[list]
[*]Menace with MelonLoader
[*]Menace ModpackLoader
[*].NET 10 runtime (bundled with tactical engine)
[/list]

[size=4][b]Documentation[/b][/size]

[list]
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_INSTALL]Installation Guide[/url]
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_MINIMAP]Tactical Minimap[/url]
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_HEATMAPS]Heatmap Renderer[/url]
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_CONFIG]Configuration[/url]
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_BOAM_ENGINE]BOAM-engine[/url]
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/CHANGELOG]Changelog[/url]
[/list]

Source code: [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack]GitHub[/url]
