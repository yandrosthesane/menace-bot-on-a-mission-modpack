#!/usr/bin/env bash
# Deploy BOAM: build ModpackLoader, copy source to staging, compile via MCP, install to game.
# Usage: ./deploy.sh
#
# Expects:
#   - Source at /home/yandros/workspace/menace_mods/<ModName>-modpack/
#   - Staging at /home/yandros/Documents/MenaceModkit/staging/<ModName>/
#   - MenaceAssetPacker repo at /home/username/workspace/menace_mods/MenaceAssetPacker/

set -euo pipefail

MOD_NAME="BOAM"
HOME="/home/yandros"
SRC_DIR="${HOME}/workspace/menace_mods/${MOD_NAME}-modpack"
STAGING_DIR="${HOME}/Documents/MenaceModkit/staging/${MOD_NAME}"
REPO_DIR="${HOME}/workspace/menace_mods/MenaceAssetPacker"
MCP_PROJECT="${REPO_DIR}/src/Menace.Modkit.Mcp/Menace.Modkit.Mcp.csproj"
GAME_MODS_DIR="${HOME}/.steam/steam/steamapps/common/Menace/Mods"
LOADER_DLL="${REPO_DIR}/src/Menace.ModpackLoader/bin/Release/net6.0/Menace.ModpackLoader.dll"
BUNDLED_DIR="${REPO_DIR}/third_party/bundled/ModpackLoader"
RUNTIME_DIR="${HOME}/Documents/MenaceModkit/runtime"

# Validate source exists
if [ ! -d "$SRC_DIR" ]; then
    echo "ERROR: Source directory not found: $SRC_DIR" >&2
    exit 1
fi

if [ ! -d "$STAGING_DIR" ]; then
    echo "==> Creating staging directory: $STAGING_DIR"
    mkdir -p "$STAGING_DIR"
fi

# Step 1: Build and deploy ModpackLoader (our version with improvements)
echo "==> Building ModpackLoader..."
if dotnet build "${REPO_DIR}/src/Menace.ModpackLoader" -c Release --nologo -v q 2>&1 | tail -3; then
    if [ -f "$LOADER_DLL" ]; then
        cp "$LOADER_DLL" "$BUNDLED_DIR/"
        cp "$LOADER_DLL" "$RUNTIME_DIR/"
        # Also update MCP bin copies so SeedBundledRuntimeDlls doesn't overwrite with stale DLL
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

# Step 2: Clean existing mod from game directory
if [ -d "${GAME_MODS_DIR}/${MOD_NAME}" ]; then
    echo "==> Removing existing ${MOD_NAME} from game mods..."
    rm -rf "${GAME_MODS_DIR:?}/${MOD_NAME:?}"
    echo "    Done."
fi

# Step 3: Copy modpack source to staging (only deployable files)
echo "==> Copying ${MOD_NAME} to staging..."
rm -rf "${STAGING_DIR:?}"/*
cp "$SRC_DIR/modpack.json" "$STAGING_DIR/"
cp "$SRC_DIR/README.md" "$STAGING_DIR/" 2>/dev/null || true
cp -r "$SRC_DIR/src" "$STAGING_DIR/" 2>/dev/null || true
cp -r "$SRC_DIR/configs" "$STAGING_DIR/" 2>/dev/null || true
cp -r "$SRC_DIR/assets" "$STAGING_DIR/" 2>/dev/null || true
echo "    Done."

# Step 4: Deploy modpack via MCP JSON-RPC (stdio)
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

echo "==> ${MOD_NAME} deployed. Restart the game to test."

# Step 5: Regenerate icons (deploy wipes Mods/BOAM/)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ICON_SCRIPT="${SCRIPT_DIR}/boam_asset_pipeline/generate-icons.sh"
if [ -x "$ICON_SCRIPT" ]; then
    echo "==> Regenerating BOAM icons..."
    "$ICON_SCRIPT" --force 2>&1 | tail -3
    echo "    Icons regenerated."
else
    echo "WARNING: Icon generation script not found at $ICON_SCRIPT" >&2
fi
