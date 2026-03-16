#!/usr/bin/env bash
# Build release archives for BOAM mod distribution.
#
# Produces (in release/):
#   BOAM-modpack-v{version}.zip                          — C# bridge mod (source + manifest)
#   BOAM-tactical-engine-v{version}-linux-x64.tar.gz     — Tactical engine + icon generator (Linux)
#   BOAM-tactical-engine-v{version}-win-x64.zip          — Tactical engine + icon generator (Windows)
#
# Prerequisites: .NET 10 SDK, jq, zip, tar

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
(cd "$RELEASE_DIR" && zip -rq "../$MOD_ZIP" "$MOD_NAME/")
echo "    $MOD_ZIP"

# ─────────────────────────────────────────────
# Archive 2: Tactical engine + icon generator
# ─────────────────────────────────────────────

build_engine_archive() {
    local RID="$1"         # linux-x64 or win-x64
    local EXT="$2"         # "" or ".exe"
    local ARCHIVE_EXT="$3" # tar.gz or zip

    echo "==> Publishing tactical engine ($RID)..."
    dotnet publish "$ENGINE_DIR/TacticalEngine.fsproj" \
        -c Release -r "$RID" --self-contained \
        -p:PublishSingleFile=false \
        -o "$RELEASE_DIR/.publish-engine-$RID" \
        -v quiet

    echo "==> Publishing icon generator ($RID)..."
    dotnet publish "$PIPELINE_DIR/BoamAssetPipeline.fsproj" \
        -c Release -r "$RID" --self-contained \
        -p:PublishSingleFile=true \
        -o "$RELEASE_DIR/.publish-icons-$RID" \
        -v quiet

    local STAGE="$RELEASE_DIR/.stage-engine-$RID/$MOD_NAME"
    mkdir -p "$STAGE/tactical_engine"

    # Tactical engine — full publish output
    cp -r "$RELEASE_DIR/.publish-engine-$RID/"* "$STAGE/tactical_engine/"

    # Icon generator — single-file binary
    cp "$RELEASE_DIR/.publish-icons-$RID/boam-icons$EXT" "$STAGE/"

    # Launcher scripts
    if [ "$RID" = "linux-x64" ]; then
        cp "$LAUNCHER_DIR/start-tactical-engine.sh" "$STAGE/"
        chmod +x "$STAGE/start-tactical-engine.sh"
        chmod +x "$STAGE/tactical_engine/TacticalEngine"
        cp "$PIPELINE_DIR/generate-icons.sh" "$STAGE/"
        chmod +x "$STAGE/generate-icons.sh"
    else
        cp "$LAUNCHER_DIR/start-tactical-engine.bat" "$STAGE/"
    fi

    # Default configs
    cp -r configs "$STAGE/"


    # Create archive
    local ARCHIVE_NAME="${MOD_NAME}-tactical-engine-v${VERSION}-${RID}"
    if [ "$ARCHIVE_EXT" = "tar.gz" ]; then
        (cd "$RELEASE_DIR/.stage-engine-$RID" && tar czf "$SCRIPT_DIR/$RELEASE_DIR/${ARCHIVE_NAME}.tar.gz" "$MOD_NAME/")
        echo "    $RELEASE_DIR/${ARCHIVE_NAME}.tar.gz"
    else
        (cd "$RELEASE_DIR/.stage-engine-$RID" && zip -rq "$SCRIPT_DIR/$RELEASE_DIR/${ARCHIVE_NAME}.zip" "$MOD_NAME/")
        echo "    $RELEASE_DIR/${ARCHIVE_NAME}.zip"
    fi
}

build_engine_archive "linux-x64" "" "zip"
build_engine_archive "win-x64" ".exe" "zip"

# ─────────────────────────────────────────────
# Cleanup staging
# ─────────────────────────────────────────────
rm -rf "$RELEASE_DIR/.stage-"* "$RELEASE_DIR/.publish-"*

echo ""
echo "==> Release v$VERSION complete:"
ls -lh "$RELEASE_DIR/"*.{zip,tar.gz} 2>/dev/null
