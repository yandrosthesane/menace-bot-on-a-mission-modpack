#!/usr/bin/env bash
# Deploy BOAM: build bridge + tactical engine + icon generator, install everything to game.
# Usage: ./deploy.sh
#
# Steps:
#   1. Build ModpackLoader DLL
#   2. Clean existing mod from game
#   3. Deploy C# bridge via MCP (compile + install)
#   4. Publish tactical engine (linux-x64, self-contained)
#   5. Publish icon generator (linux-x64, single-file)
#   6. Install engine + icons tool + launcher + config to game
#   7. Regenerate icons

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

MOD_NAME="BOAM"
USER_HOME="/home/yandros"
SRC_DIR="${SCRIPT_DIR}"
STAGING_DIR="${USER_HOME}/Documents/MenaceModkit/staging/${MOD_NAME}"
REPO_DIR="${USER_HOME}/workspace/menace_mods/MenaceAssetPacker"
MCP_PROJECT="${REPO_DIR}/src/Menace.Modkit.Mcp/Menace.Modkit.Mcp.csproj"
GAME_DIR="${USER_HOME}/.steam/steam/steamapps/common/Menace"
GAME_MODS_DIR="${GAME_DIR}/Mods"
GAME_MOD_DIR="${GAME_MODS_DIR}/${MOD_NAME}"
LOADER_DLL="${REPO_DIR}/src/Menace.ModpackLoader/bin/Release/net6.0/Menace.ModpackLoader.dll"
BUNDLED_DIR="${REPO_DIR}/third_party/bundled/ModpackLoader"
RUNTIME_DIR="${USER_HOME}/Documents/MenaceModkit/runtime"

ENGINE_DIR="boam_tactical_engine"
PIPELINE_DIR="boam_asset_pipeline"
LAUNCHER_DIR="launcher"
PUBLISH_ENGINE=".publish-engine"
PUBLISH_ICONS=".publish-icons"

# Validate source exists
if [ ! -f "$SRC_DIR/modpack.json" ]; then
    echo "ERROR: modpack.json not found in $SRC_DIR" >&2
    exit 1
fi

if [ ! -d "$STAGING_DIR" ]; then
    echo "==> Creating staging directory: $STAGING_DIR"
    mkdir -p "$STAGING_DIR"
fi

# ─────────────────────────────────────────────
# Step 1: Build ModpackLoader
# ─────────────────────────────────────────────
echo "==> Building ModpackLoader..."
if dotnet build "${REPO_DIR}/src/Menace.ModpackLoader" -c Release --nologo -v q 2>&1 | tail -3; then
    if [ -f "$LOADER_DLL" ]; then
        cp "$LOADER_DLL" "$BUNDLED_DIR/"
        cp "$LOADER_DLL" "$RUNTIME_DIR/"
        for d in "${REPO_DIR}"/src/Menace.Modkit.Mcp/bin/*/net*/third_party/bundled/ModpackLoader/; do
            [ -d "$d" ] && cp "$LOADER_DLL" "$d"
        done
        echo "    ModpackLoader updated in all locations."
    else
        echo "WARNING: ModpackLoader DLL not found at $LOADER_DLL" >&2
    fi
else
    echo "WARNING: ModpackLoader build failed — using existing DLL" >&2
fi

# ─────────────────────────────────────────────
# Step 2: Clean existing mod from game
# ─────────────────────────────────────────────
if [ -d "$GAME_MOD_DIR" ]; then
    echo "==> Removing existing ${MOD_NAME} from game mods..."
    rm -rf "${GAME_MOD_DIR:?}"
    echo "    Done."
fi

# ─────────────────────────────────────────────
# Step 3: Deploy C# bridge via MCP (compile + install)
# ─────────────────────────────────────────────
echo "==> Copying ${MOD_NAME} source to staging..."
rm -rf "${STAGING_DIR:?}"/*
cp "$SRC_DIR/modpack.json" "$STAGING_DIR/"
cp "$SRC_DIR/README.md" "$STAGING_DIR/" 2>/dev/null || true
cp -r "$SRC_DIR/src" "$STAGING_DIR/" 2>/dev/null || true
cp -r "$SRC_DIR/configs" "$STAGING_DIR/" 2>/dev/null || true
cp -r "$SRC_DIR/assets" "$STAGING_DIR/" 2>/dev/null || true
echo "    Done."

echo "==> Deploying ${MOD_NAME} via MCP (compile + install)..."
python3 -c "
import subprocess, json, time, select, sys

proc = subprocess.Popen(
    ['dotnet', 'run', '--project', '$MCP_PROJECT', '--no-build'],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL
)

def send(msg):
    proc.stdin.write(json.dumps(msg).encode() + b'\n')
    proc.stdin.flush()

def recv_jsonrpc(target_id, timeout=30):
    deadline = time.time() + timeout
    while time.time() < deadline:
        ready, _, _ = select.select([proc.stdout], [], [], 1)
        if ready:
            line = proc.stdout.readline().decode().strip()
            if not line: continue
            try:
                parsed = json.loads(line)
                if isinstance(parsed, dict) and parsed.get('id') == target_id:
                    return parsed
            except json.JSONDecodeError:
                pass
    return None

# Handshake
send({'jsonrpc':'2.0','id':1,'method':'initialize','params':{'protocolVersion':'2024-11-05','capabilities':{},'clientInfo':{'name':'cli','version':'1.0'}}})
recv_jsonrpc(1)
send({'jsonrpc':'2.0','method':'notifications/initialized'})
time.sleep(0.3)

# Deploy
send({'jsonrpc':'2.0','id':2,'method':'tools/call','params':{'name':'deploy_modpack','arguments':{'name':'$MOD_NAME'}}})
r = recv_jsonrpc(2, timeout=45)

proc.terminate()

if not r:
    print('ERROR: No response from MCP server', file=sys.stderr)
    sys.exit(1)

content = r.get('result',{}).get('content',[{}])[0].get('text','')
try:
    data = json.loads(content)
    print(json.dumps(data, indent=2))
    if not data.get('success', False):
        sys.exit(1)
except json.JSONDecodeError:
    print(content)
    sys.exit(1)
"

echo "==> C# bridge deployed."

# ─────────────────────────────────────────────
# Step 4: Publish tactical engine (linux-x64)
# ─────────────────────────────────────────────
echo "==> Publishing tactical engine (linux-x64)..."
dotnet publish "$ENGINE_DIR/TacticalEngine.fsproj" \
    -c Release -r linux-x64 --self-contained \
    -p:PublishSingleFile=false \
    -o "$PUBLISH_ENGINE" \
    -v quiet

# ─────────────────────────────────────────────
# Step 5: Publish icon generator (linux-x64)
# ─────────────────────────────────────────────
echo "==> Publishing icon generator (linux-x64)..."
dotnet publish "$PIPELINE_DIR/BoamAssetPipeline.fsproj" \
    -c Release -r linux-x64 --self-contained \
    -p:PublishSingleFile=true \
    -o "$PUBLISH_ICONS" \
    -v quiet

# ─────────────────────────────────────────────
# Step 6: Install engine + tools to game
# ─────────────────────────────────────────────
echo "==> Installing tactical engine to ${GAME_MOD_DIR}..."
mkdir -p "$GAME_MOD_DIR/tactical_engine"

# Tactical engine — full publish output
cp -r "$PUBLISH_ENGINE/"* "$GAME_MOD_DIR/tactical_engine/"
chmod +x "$GAME_MOD_DIR/tactical_engine/TacticalEngine"

# Icon generator — single-file binary
cp "$PUBLISH_ICONS/boam-icons" "$GAME_MOD_DIR/"
chmod +x "$GAME_MOD_DIR/boam-icons"

# Launcher script
cp "$LAUNCHER_DIR/start-tactical-engine.sh" "$GAME_MOD_DIR/"
chmod +x "$GAME_MOD_DIR/start-tactical-engine.sh"

# Default configs
mkdir -p "$GAME_MOD_DIR/configs"
cp configs/config.json5 configs/tactical_map.json5 configs/tactical_map_presets.json5 "$GAME_MOD_DIR/configs/"
sed 's|/home/user/|/home/yandros/|g' configs/icon-config.json5 > "$GAME_MOD_DIR/configs/icon-config.json5"
echo "    All configs installed to configs/"

echo "    Done."

# Cleanup publish dirs
rm -rf "$PUBLISH_ENGINE" "$PUBLISH_ICONS"

# ─────────────────────────────────────────────
# Step 7: Regenerate icons
# ─────────────────────────────────────────────
echo "==> Regenerating icons..."
# Use user icon-config from UserData/BOAM/configs if it exists
USER_ICON_CONFIG="$GAME_DIR/UserData/BOAM/configs/icon-config.json5"
DEFAULT_ICON_CONFIG="$GAME_MOD_DIR/configs/icon-config.json5"
if [ -f "$USER_ICON_CONFIG" ]; then
    ICON_CONFIG="$USER_ICON_CONFIG"
    echo "    Using user icon-config: $ICON_CONFIG"
else
    ICON_CONFIG="$DEFAULT_ICON_CONFIG"
fi
"$GAME_MOD_DIR/boam-icons" --force --config "$ICON_CONFIG" 2>&1 | tail -5
echo "    Icons regenerated."

echo ""
echo "==> ${MOD_NAME} fully deployed. Restart the game to test."
