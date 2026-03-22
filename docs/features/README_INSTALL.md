# Installation Guide

## Prerequisites

- [Menace](https://store.steampowered.com/app/2432860/Menace/) installed via Steam
- [MelonLoader](https://melonwiki.xyz/) installed in the game directory
- [Menace ModpackLoader](https://github.com/YandrosTheSane/menace-modpack-loader) installed (`Menace.ModpackLoader.dll` in `Mods/`)
- [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0) — required for the BOAM-engine (slim variant). Not needed if using the bundled variant.

## Step 1: Install the BOAM-modpack

Use the Menace Modkit to deploy the BOAM modpack.

## Step 2: Install the BOAM-engine

Download the BOAM-engine archive for your platform and extract it into `Mods/BOAM/`.

### Which variant?

| Variant | Size | Best for |
|---------|------|----------|
| **slim** | ~5 MB | Recommended — small download, no antivirus false positives. Requires [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0). |
| **bundled** | ~112 MB | Fallback if you can't install .NET 10. Includes the runtime, nothing else to install. Some antivirus may flag bundled .NET system DLLs — these are standard Microsoft files. |

Try **slim** first. If the engine fails to start with a missing framework error, either install [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) or switch to the **bundled** variant.

### Installing the .NET 10 runtime (slim variant only)

<details>
<summary>Linux</summary>

```bash
# Ubuntu/Debian
sudo apt install dotnet-runtime-10.0

# Fedora
sudo dnf install dotnet-runtime-10.0

# Or install from Microsoft: https://dotnet.microsoft.com/download/dotnet/10.0
```

</details>

<details>
<summary>Windows</summary>

```bat
winget install Microsoft.DotNet.Runtime.10
```

Or download the installer from [dotnet.microsoft.com/download/dotnet/10.0](https://dotnet.microsoft.com/download/dotnet/10.0).

</details>

### Extract the archive

<details>
<summary>Linux</summary>

```bash
# Bundled
unzip BOAM-tactical-engine-v1.3.0-linux-x64-bundled.zip -d /path/to/Menace/Mods/BOAM/

# Slim
unzip BOAM-tactical-engine-v1.3.0-linux-x64-slim.zip -d /path/to/Menace/Mods/BOAM/
```

</details>

<details>
<summary>Windows</summary>

Unzip `BOAM-tactical-engine-v1.3.0-win-x64-bundled.zip` (or `-slim.zip`) into `Mods\BOAM\`.

</details>

Replace `v1.3.0` with the version you downloaded. Usage is identical for both variants — the launcher script and all commands work the same way.

## Step 3: Extract Game Art

BOAM does not ship game art. Extract badge and faction icon PNGs from the game data using the modkit asset extractor. Do not share those assets online.

The extracted data is placed by the modkit into:
```
Menace/UserData/ExtractedData/Assets/
```

BOAM reads source icons from:
```
Assets/Resources/ui/sprites/badges/       → squad badges, leader badges
Assets/Resources/ui/sprites/factions/     → faction icons
```

No manual copying is needed — the icon generator reads directly from the extracted data location.

## Step 4: Generate Icons

Icons are generated automatically by the tactical engine on first startup if none are found. You can also run the generator manually:

<details>
<summary>Linux</summary>

```bash
cd /path/to/Menace/Mods/BOAM/
./boam-icons --force
```

</details>

<details>
<summary>Windows</summary>

```bat
cd C:\path\to\Menace\Mods\BOAM\
boam-icons.exe --force
```

</details>

This reads `configs/icon-config.json5` and produces 64x64 icons into the persistent data directory:
```
UserData/BOAM/icons/
├── factions/          Faction fallback icons (wildlife.png, player.png, ...)
└── templates/         Per-unit icons (alien_stinger.png, carda.png, ...)
```

Icons survive mod deploys (stored in `UserData/`, not `Mods/`). Resolution chain: leader → template → faction → colored dot.

See [Icon Generator](README_ICON_GENERATOR.md) for config format and adding new units.

## Step 5: Set Up Steam Launch (Recommended)

Configure Steam to automatically start the tactical engine alongside the game.

**Right-click Menace in Steam → Properties → General → Launch Options**, then set:

<details>
<summary>Linux</summary>

```
/path/to/Menace/Mods/BOAM/boam-launch.sh %command%
```

Example with default Steam library path:
```
~/.local/share/Steam/steamapps/common/Menace/Mods/BOAM/boam-launch.sh %command%
```

</details>

<details>
<summary>Windows</summary>

```
"C:\Program Files (x86)\Steam\steamapps\common\Menace\Mods\BOAM\boam-launch.bat" %command%
```

Adjust the path if your Steam library is in a different location.

</details>

This will:
1. Start the BOAM Tactical Engine in a separate window
2. Launch the game
3. Shut down the engine when the game exits

To start the engine manually without Steam integration, use `start-tactical-engine.sh` (Linux) or `start-tactical-engine.bat` (Windows) directly.

## Step 6: Customise Configs (Optional)

On first run, all configs are automatically seeded into `UserData/BOAM/configs/` from mod defaults. Edit them to customise keybindings, display presets, and visual settings — user configs survive deploys.
If a mod update changes the config structure (version bump), the mod default is used instead and a warning is logged. Update your user config to match the new structure and bump its `configVersion`.

See [Configuration](README_CONFIG.md) for all options.

## Updating

When updating BOAM to a new version:

1. **Re-deploy** the BOAM-modpack (step 1)
2. **Re-extract** the BOAM-engine (step 2)

Icons are regenerated automatically by the tactical engine on startup if missing. Configs in `UserData/BOAM/` are never touched — if a mod update changes the config structure, a warning is logged and the mod default is used until you update your user config (see [Configuration](README_CONFIG.md)).

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
