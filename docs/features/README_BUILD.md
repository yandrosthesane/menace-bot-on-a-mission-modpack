---
order: 9
---

# Building from Source

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git

## Clone

```bash
git clone https://github.com/yandrosthesane/menace-bot-on-a-mission-modpack.git
cd menace-bot-on-a-mission-modpack
```

## Tactical Engine

```bash
# Linux
dotnet publish boam_tactical_engine/TacticalEngine.fsproj -c Release -r linux-x64 --self-contained -o publish/engine

# Windows
dotnet publish boam_tactical_engine/TacticalEngine.fsproj -c Release -r win-x64 --self-contained -o publish/engine
```

Copy `publish/engine/` contents to `Mods/BOAM/tactical_engine/`.

## Icon Generator

```bash
# Linux
dotnet publish boam_asset_pipeline/BoamAssetPipeline.fsproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish/icons

# Windows
dotnet publish boam_asset_pipeline/BoamAssetPipeline.fsproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/icons
```

Copy `publish/icons/boam-icons` (or `boam-icons.exe`) to `Mods/BOAM/`.

## Slim Variant

For a smaller build that requires the .NET 10 runtime to be installed:

```bash
dotnet publish boam_tactical_engine/TacticalEngine.fsproj -c Release -r linux-x64 --no-self-contained -o publish/engine-slim
dotnet publish boam_asset_pipeline/BoamAssetPipeline.fsproj -c Release -r linux-x64 --no-self-contained -p:PublishSingleFile=true -o publish/icons-slim
```

## All Release Archives

To build all release archives (Linux + Windows, bundled + slim) at once:

```bash
./release.sh
```

Output goes to `release/`.
