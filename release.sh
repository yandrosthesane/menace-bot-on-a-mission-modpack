#!/usr/bin/env bash
# Build release archives for BOAM mod distribution.
#
# Produces (in release/):
#   BOAM-modpack-v{version}.zip                                    — C# bridge mod (source + manifest)
#   BOAM-tactical-engine-v{version}-linux-x64-bundled.zip          — Engine + runtime (Linux, ~112MB)
#   BOAM-tactical-engine-v{version}-win-x64-bundled.zip            — Engine + runtime (Windows, ~112MB)
#   BOAM-tactical-engine-v{version}-linux-x64-slim.zip             — Engine only (Linux, ~5MB, requires .NET 10)
#   BOAM-tactical-engine-v{version}-win-x64-slim.zip               — Engine only (Windows, ~5MB, requires .NET 10)
#
# Prerequisites: .NET 10 SDK, jq, zip

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

MOD_NAME=$(jq -r '.name' modpack.json)
VERSION=$(jq -r '.version' modpack.json)
RELEASE_DIR="release"
ENGINE_DIR="boam_tactical_engine"
PIPELINE_DIR="boam_asset_pipeline"
LAUNCHER_DIR="launcher"

echo "==> Building $MOD_NAME release v$VERSION"
echo ""

# ─────────────────────────────────────────────
# Archive 1: C# bridge mod (source-distributed)
# ─────────────────────────────────────────────
echo "==> Packaging mod bridge..."

STAGE_MOD="$RELEASE_DIR/$MOD_NAME"
rm -rf "$STAGE_MOD"
mkdir -p "$STAGE_MOD"

cp modpack.json "$STAGE_MOD/"
cp README*.md "$STAGE_MOD/"
cp -r src "$STAGE_MOD/"

MOD_ZIP="$RELEASE_DIR/${MOD_NAME}-modpack-v${VERSION}.zip"
rm -f "$MOD_ZIP"
(cd "$RELEASE_DIR" && zip -qr9 "../$MOD_ZIP" "$MOD_NAME/")
echo "    $MOD_ZIP"

# ─────────────────────────────────────────────
# Archive 2: Tactical engine + icon generator
# ─────────────────────────────────────────────

build_engine_archive() {
    local RID="$1"         # linux-x64 or win-x64
    local EXT="$2"         # "" or ".exe"
    local VARIANT="$3"     # bundled or slim

    local SELF_CONTAINED
    local SUFFIX
    if [ "$VARIANT" = "bundled" ]; then
        SELF_CONTAINED="--self-contained"
        SUFFIX="bundled"
    else
        SELF_CONTAINED="--no-self-contained"
        SUFFIX="slim"
    fi

    echo "==> Publishing tactical engine ($RID, $SUFFIX)..."
    dotnet publish "$ENGINE_DIR/TacticalEngine.fsproj" \
        -c Release -r "$RID" $SELF_CONTAINED \
        -p:PublishSingleFile=false \
        -o "$RELEASE_DIR/.publish-engine-$RID-$SUFFIX" \
        -v quiet

    echo "==> Publishing icon generator ($RID, $SUFFIX)..."
    dotnet publish "$PIPELINE_DIR/BoamAssetPipeline.fsproj" \
        -c Release -r "$RID" $SELF_CONTAINED \
        -p:PublishSingleFile=true \
        -o "$RELEASE_DIR/.publish-icons-$RID-$SUFFIX" \
        -v quiet

    local STAGE="$RELEASE_DIR/.stage-engine-$RID-$SUFFIX/$MOD_NAME"
    mkdir -p "$STAGE/tactical_engine"

    # Tactical engine — full publish output
    cp -r "$RELEASE_DIR/.publish-engine-$RID-$SUFFIX/"* "$STAGE/tactical_engine/"

    # Icon generator — single-file binary
    cp "$RELEASE_DIR/.publish-icons-$RID-$SUFFIX/boam-icons$EXT" "$STAGE/"

    # Launcher scripts
    if [ "$RID" = "linux-x64" ]; then
        cp "$LAUNCHER_DIR/start-tactical-engine.sh" "$STAGE/"
        cp "$LAUNCHER_DIR/boam-launch.sh" "$STAGE/"
        chmod +x "$STAGE/start-tactical-engine.sh"
        chmod +x "$STAGE/boam-launch.sh"
        chmod +x "$STAGE/tactical_engine/TacticalEngine"
        chmod +x "$STAGE/boam-icons"
    else
        cp "$LAUNCHER_DIR/start-tactical-engine.bat" "$STAGE/"
        cp "$LAUNCHER_DIR/boam-launch.bat" "$STAGE/"
    fi

    # Default configs
    cp -r configs "$STAGE/"

    # Create archive
    local ARCHIVE_NAME="${MOD_NAME}-tactical-engine-v${VERSION}-${RID}-${SUFFIX}"
    (cd "$RELEASE_DIR/.stage-engine-$RID-$SUFFIX" && zip -qr9 "$SCRIPT_DIR/$RELEASE_DIR/${ARCHIVE_NAME}.zip" "$MOD_NAME/")
    echo "    $RELEASE_DIR/${ARCHIVE_NAME}.zip"
}

# Bundled (self-contained — includes .NET runtime)
build_engine_archive "linux-x64" "" "bundled"
build_engine_archive "win-x64" ".exe" "bundled"

# Slim (framework-dependent — requires .NET 10 runtime installed)
build_engine_archive "linux-x64" "" "slim"
build_engine_archive "win-x64" ".exe" "slim"

# ─────────────────────────────────────────────
# Cleanup staging
# ─────────────────────────────────────────────
rm -rf "$RELEASE_DIR/.stage-"* "$RELEASE_DIR/.publish-"*

echo ""
echo "==> Release v$VERSION complete:"
ls -lh "$RELEASE_DIR/"*.zip 2>/dev/null
