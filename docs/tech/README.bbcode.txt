[size=5][b]BOAM — Bot On A Mission[/b][/size]

[b]Version:[/b] 1.2.0 | [url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack]Documentation[/url] | [url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/CHANGELOG]Changelog[/url]

AI behavior analysis mod for Menace.
Intercepts AI decision-making at runtime, captures tactical data for offline heatmap rendering, provides a real-time in-game minimap overlay, and records full battle sessions (player actions + AI decisions + combat outcomes).

[size=4][b]Features[/b][/size]

[list]
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_MINIMAP][b]Tactical Minimap[/b][/url] — In-game IMGUI overlay showing unit positions on the captured map background
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_HEATMAPS][b]Heatmap Renderer[/b][/url] — Offline heatmap generation from deferred render jobs — tile scores, decisions, movement
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_BOAM_ENGINE][b]Action Logging[/b][/url] — Records player actions, AI decisions, and combat outcomes to JSONL battle logs
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_CONFIG][b]Configuration[/b][/url] — Versioned JSON5 configs with user/mod-default two-tier system
[/list]

[size=4][b]Components[/b][/size]

[list]
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_BOAM_MODPACK][b]BOAM-modpack[/b][/url] — In-game mod (MelonLoader/Wine): Harmony patches, minimap overlay, map capture, action forwarding
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_BOAM_ENGINE][b]BOAM-engine[/b][/url] — External companion (.NET 10, port 7660): heatmap renderer, action logger, auto-navigation
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_ICON_GENERATOR][b]Icon Generator[/b][/url] — CLI tool: resizes game badge art into heatmap/minimap icons
[/list]

The BOAM-modpack works standalone — the minimap needs no engine. Start the BOAM-engine only when you want heatmaps or action logging.

[b]First time?[/b] Follow the [url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_INSTALL]Installation Guide[/url].

[size=4][b]Install Layout[/b][/size]

[code]
Menace/
├── Mods/BOAM/
│   ├── dlls/BOAM.dll              C# bridge (compiled by ModpackLoader)
│   ├── modpack.json               Mod manifest
│   ├── configs/                   Mod default configs (reset on deploy)
│   │   ├── config.json5           Engine ports, rendering, heatmaps toggle
│   │   ├── tactical_map.json5     Minimap keybindings, visual defaults
│   │   ├── tactical_map_presets.json5  Display presets (sizes, styles, anchors)
│   │   └── icon-config.json5      Icon generation source mappings
│   ├── tactical_engine/           Engine binary + runtime
│   │   └── TacticalEngine(.exe)
│   ├── start-tactical-engine.sh   Launcher (opens terminal, logs to file)
│   ├── boam-icons(.exe)           Icon generator
│   ├── icons/                     Generated heatmap/minimap icons
│   │   ├── factions/
│   │   └── templates/
│   └── logs/                      Engine log (overwritten each run)
│       └── tactical_engine.log
└── UserData/BOAM/
    ├── configs/                   User configs (persistent, checked first)
    ├── badges/                    Source art for icon generation
    ├── factions/
    └── battle_reports/            Recorded battles (auto-created per session)
        └── battle_YYYY_MM_DD_HH_MM/
            ├── mapbg.png          Captured map background
            ├── mapbg.info         Tile dimensions
            ├── mapdata.bin        Binary tile data
            ├── dramatis_personae.json  Actor registry
            ├── round_log.jsonl    Action log
            ├── render_jobs/       Self-contained render job JSON files
            └── heatmaps/          Rendered heatmap PNGs
[/code]

[size=4][b]Usage[/b][/size]

[size=3][b]Start the Engine[/b][/size]

[code]
# Linux
./start-tactical-engine.sh                              # passive
./start-tactical-engine.sh --on-title /navigate/tactical # auto-navigate

# Windows
start-tactical-engine.bat                                # passive
start-tactical-engine.bat --on-title /navigate/tactical  # auto-navigate
[/code]

Then launch the game normally through Steam. On Linux the engine opens in its own terminal window; on Windows it runs in the command prompt. Logs written to [i]Mods/BOAM/logs/tactical_engine.log[/i].

[size=3][b]In-Game Minimap[/b][/size]

[list]
[*][b]M[/b] — Toggle minimap on/off
[*][b]L[/b] — Cycle display presets (size/anchor)
[/list]

Additional keys (FoW, labels, etc.) can be enabled in [i]tactical_map.json5[/i]. See [url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_MINIMAP]Tactical Minimap[/url].

[size=3][b]Render Heatmaps[/b][/size]

After playing a round, render job data is flushed to disk. Render heatmaps on demand:

[code]
# Linux
./tactical_engine/TacticalEngine --render battle_2026_03_15_15_14
./tactical_engine/TacticalEngine --render battle_2026_03_15_15_14 --pattern "r01_*"
./tactical_engine/TacticalEngine --render battle_2026_03_15_15_14 --pattern "*_alien_stinger*"

# Windows
tactical_engine\TacticalEngine.exe --render battle_2026_03_15_15_14
tactical_engine\TacticalEngine.exe --render battle_2026_03_15_15_14 --pattern "r01_*"
tactical_engine\TacticalEngine.exe --render battle_2026_03_15_15_14 --pattern "*_alien_stinger*"

# HTTP (any platform, while engine is running)
curl -s -X POST http://127.0.0.1:7660/render/battle/battle_2026_03_15_15_14 -d '{}'
[/code]

See [url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_HEATMAPS]Heatmap Renderer[/url].

[size=3][b]Generate Icons[/b][/size]

[code]
# Linux
./boam-icons --force

# Windows
boam-icons.exe --force
[/code]

See [url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_ICON_GENERATOR]Icon Generator[/url].

[size=4][b]Download Variants[/b][/size]

Two BOAM-engine variants are available:
[list]
[*][b]Slim[/b] (~5 MB) — recommended. Requires [url=https://dotnet.microsoft.com/download/dotnet/10.0].NET 10 runtime[/url] installed on your system. No antivirus false positives.
[*][b]Bundled[/b] (~112 MB) — fallback if you can't install .NET 10. Includes the runtime. Some virus scanners may flag bundled .NET system DLLs — these are standard Microsoft runtime files.
[/list]

[size=4][b]Requirements[/b][/size]

[list]
[*]Menace with MelonLoader
[*]Menace ModpackLoader
[*][url=https://dotnet.microsoft.com/download/dotnet/10.0].NET 10 runtime[/url] (for slim variant, or use bundled)
[/list]

[size=4][b]Changelog[/b][/size]

[size=3][b]v1.2.0[/b][/size]
[list]
[*][b]Standalone minimap[/b] — Minimap works without the BOAM-engine. Only start the engine for heatmaps or action logging.
[*][b]Bounded context architecture[/b] — Both BOAM-modpack and BOAM-engine reorganized into independent bounded contexts.
[*][b]Config auto-seeding[/b] — Configs automatically copied to UserData on first run. No manual setup needed.
[*][b]Slim engine variant[/b] — ~5 MB download if you have .NET 10 installed. Bundled variant (~112 MB) also available.
[*][b]Engine startup banner[/b] — Shows config source and active/inactive features at launch.
[*][b]Replay system removed[/b] — Experimental replay removed. Action logging and AI capture remain.
[/list]

Full changelog: [url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/CHANGELOG]Changelog[/url]

[size=4][b]Related Mods[/b][/size]

[list]
[*][url=https://www.nexusmods.com/menace/mods/86]Tactical Map ~ By YandrosTheSane[/url] — Standalone minimap mod. The tactical minimap feature in BOAM supersedes this mod. Tactical Map will not be updated further.
[*][url=https://www.nexusmods.com/menace/mods/73]PeekABoo ~ By YandrosTheSane[/url] — AI awareness tool. Will be reimplemented within BOAM's analysis framework.
[/list]

[size=4][b]Documentation[/b][/size]

[list]
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_INSTALL]Installation Guide[/url] — Setup, asset extraction, icon generation, shell shortcuts
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_MINIMAP]Tactical Minimap[/url] — In-game overlay controls, display presets, customization
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_HEATMAPS]Heatmap Renderer[/url] — Render API, pattern matching, what each heatmap shows
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_CONFIG]Configuration[/url] — Two-tier config system, versioning, all config options
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_BOAM_MODPACK]BOAM-modpack[/url] — In-game mod: minimap, hooks, map capture
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_BOAM_ENGINE]BOAM-engine[/url] — External engine: heatmaps, logging, CLI, HTTP API
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/README_ICON_GENERATOR]Icon Generator[/url] — Config format, fallback chain, customization
[*][url=https://yandrosthesane.github.io/menace-bot-on-a-mission-modpack/docs/features/CHANGELOG]Changelog[/url] — Version history
[/list]

Source code: [url=https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack]GitHub[/url]
