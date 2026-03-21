# Installation Guide

## Prerequisites

- [Menace](https://store.steampowered.com/app/2432860/Menace/) installed via Steam
- [MelonLoader](https://melonwiki.xyz/) installed in the game directory
- [Menace ModpackLoader](https://github.com/YandrosTheSane/menace-modpack-loader) installed (`Menace.ModpackLoader.dll` in `Mods/`)

## Step 1: Install the C# Bridge Plugin

Use the Menace Modkit to deploy the BOAM modpack.

## Step 2: Install the Tactical Engine

Download the tactical engine archive for your platform and extract it into `Mods/BOAM/`.

### Which variant?

| Variant | Size | Best for |
|---------|------|----------|
| **bundled** | ~112 MB | Most users — includes everything, no extra setup |
| **slim** | ~5 MB | Users who already have .NET 10 or prefer smaller downloads |

**Bundled** includes the .NET 10 runtime inside the archive. Extract and run — nothing else to install. Some antivirus software may flag the bundled .NET system DLLs as suspicious; these are standard Microsoft runtime files.

**Slim** is the same engine without the runtime. It requires [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) installed on your system. Much smaller download, no antivirus false positives.

### Installing the .NET 10 runtime (slim variant only)

**Linux:**
```bash
# Ubuntu/Debian
sudo apt install dotnet-runtime-10.0

# Fedora
sudo dnf install dotnet-runtime-10.0

# Or install from Microsoft: https://dotnet.microsoft.com/download/dotnet/10.0
```

**Windows:**
```bat
winget install Microsoft.DotNet.Runtime.10
```

Or download the installer from [dotnet.microsoft.com/download/dotnet/10.0](https://dotnet.microsoft.com/download/dotnet/10.0).

### Extract the archive

**Linux:**
```bash
# Bundled
unzip BOAM-tactical-engine-v1.2.0-linux-x64-bundled.zip -d /path/to/Menace/Mods/BOAM/

# Slim
unzip BOAM-tactical-engine-v1.2.0-linux-x64-slim.zip -d /path/to/Menace/Mods/BOAM/
```

**Windows:**

Unzip `BOAM-tactical-engine-v1.2.0-win-x64-bundled.zip` (or `-slim.zip`) into `Mods\BOAM\`.

Usage is identical for both variants — the launcher script and all commands work the same way.

## Step 3: Extract Game Art

BOAM does not ship game art.
Use custom files or extract badge and faction icon PNGs from the game data using the modkit asset extractor.
Do not share those assets online.

If using the first option, the source locations in the extracted data folder:
```
Assets/Resources/ui/sprites/badges/       → squad badges, leader badges
Assets/Resources/ui/sprites/factions/     → faction icons
```

Copy the extracted PNGs into the persistent assets directory:
```
Menace/UserData/BOAM/
├── badges/                    Squad badges (234x234 PNGs)
│   ├── squad_badge_bugs_stinger_234x234.png
│   ├── squad_badge_bugs_dragonfly_234x234.png
│   ├── leaders/
│   │   ├── carda_badge_234x234.png
│   │   └── ...
│   └── ...
└── factions/                  Faction icons
    ├── enemy_faction_01.png
    ├── faction_icon_hud_01.png
    └── ...
```

## Step 4: Generate Icons

Run the icon generator to resize the extracted art into heatmap/minimap icons:

```bash
cd /path/to/Menace/Mods/BOAM/
./boam-icons --force          # Linux
boam-icons.exe --force        # Windows
```

This reads `configs/icon-config.json5` and produces 64x64 icons:
```
Mods/BOAM/icons/
├── factions/          Faction fallback icons (wildlife.png, player.png, ...)
└── templates/         Per-unit icons (alien_stinger.png, carda.png, ...)
```

Icons are used by both the minimap overlay and the heatmap renderer. Resolution chain: leader → template → faction → colored dot.

See [Icon Generator](README_ICON_GENERATOR.md) for config format and adding new units.

## Step 5: Set Up Shell Shortcuts (Optional)

Add these to your `~/.bashrc` or `~/.zshrc` so you can run BOAM tools from anywhere:

```bash
# Adjust MENACE_DIR if your Steam library is in a different location
export MENACE_DIR="$HOME/.local/share/Steam/steamapps/common/Menace"
export BOAM_DIR="$MENACE_DIR/Mods/BOAM"

# Start the tactical engine (accepts same args as start-tactical-engine.sh)
alias boam-engine='$BOAM_DIR/start-tactical-engine.sh'

# Regenerate icons
alias boam-icons='$BOAM_DIR/boam-icons'
```

Then from any directory:
```bash
boam-engine                                          # passive start
boam-engine --on-title /navigate/tactical            # auto-navigate to tactical
boam-icons --force                                   # regenerate icons
```

## Step 6: Customise Configs (Optional)

On first run, all configs are automatically seeded into `UserData/BOAM/configs/` from mod defaults. Edit them to customise keybindings, display presets, and visual settings — user configs survive deploys.
If a mod update changes the config structure (version bump), the mod default is used instead and a warning is logged. Update your user config to match the new structure and bump its `configVersion`.

See [Configuration](README_CONFIG.md) for all options.

## Updating
When updating BOAM to a new version:

1. **Re-deploy** the bridge plugin (step 1) — this wipes `Mods/BOAM/`
2. **Re-extract** the tactical engine (step 2)
3. **Re-generate** icons (step 4) — deploy wipes `icons/`
4. **Check config versions** — if the game logs a warning about outdated configs, update your user configs in `UserData/BOAM/configs/` to match the new structure and bump `configVersion`
5. Shell shortcuts (step 5) only need to be set up once — they survive deploys

Your user configs and source art in `UserData/BOAM/` are never touched by deploys.

## Troubleshooting

### Windows: Compilation fails with `ConcurrentQueue<T>` conflict

If the modkit reports a compile error like:

> The type 'ConcurrentQueue\<T>' exists in both 'MonoMod.Backports' and 'System.Collections.Concurrent'

This is caused by the game's `MelonLoader/net35` directory containing backported types that conflict with the .NET 6 BCL. To fix it:

1. **Navigate to your game's MelonLoader directory:**: `Menace\MelonLoader\`
2. **Delete the `net35` folder** — it is not used by Menace (Il2Cpp/.NET 6 only).

### Windows: `MelonLoader/net6 directory not found` in modkit bundled path

If the modkit reports:

> MelonLoader/net6 directory not found at ...\third_party\bundled\MelonLoader\net6

The bundled MelonLoader references are missing. To fix it:

1. **Copy your game's `MelonLoader\net6` folder** into the modkit's bundled directory:
   ```
   copy from:  Menace\MelonLoader\net6\
   copy to:    menace-modkit-win-x64\gui-win-x64\third_party\bundled\MelonLoader\net6\
   ```
   Create the `MelonLoader` folder inside `third_party\bundled\` if it doesn't exist.
2. Re-run the deploy.
