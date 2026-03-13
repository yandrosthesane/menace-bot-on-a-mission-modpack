#!/usr/bin/env bash
# Generate BOAM heatmap icons from game assets.
# Reads icon-config.json, resizes source PNGs to target size via ffmpeg.
# Usage: ./generate-icons.sh [--force]
#   --force: overwrite existing output files (default: skip existing)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
CONFIG="$SCRIPT_DIR/icon-config.json"

if [ ! -f "$CONFIG" ]; then
    echo "ERROR: Config not found: $CONFIG" >&2
    exit 1
fi

FORCE=false
[ "${1:-}" = "--force" ] && FORCE=true

# Parse config and emit lines: source_dir|source_rel|output_rel|size
entries=$(python3 -c "
import json, sys
c = json.load(open('$CONFIG'))
sources = c['sources']
size = c['defaults']['size']
for section in ['factions', 'templates', 'leaders']:
    for e in c.get(section, []):
        d = e.get('dir', '')
        base = sources.get(d, '')
        if not base:
            print(f'WARN: unknown source dir \"{d}\" for {e[\"output\"]}', file=sys.stderr)
            continue
        sz = e.get('size', size)
        print(f'{base}|{e[\"source\"]}|{e[\"output\"]}|{sz}')
")

OUTPUT_BASE=$(python3 -c "import json; print(json.load(open('$CONFIG'))['defaults']['output_base'])")
SIZE=$(python3 -c "import json; print(json.load(open('$CONFIG'))['defaults']['size'])")

echo "Icon generation: default ${SIZE}x${SIZE}"
echo "  Output: $OUTPUT_BASE"
echo ""

generated=0
skipped=0
errors=0

echo "$entries" | while IFS='|' read -r base src_rel out_rel sz; do
    src="$base/$src_rel"
    out="$OUTPUT_BASE/$out_rel"

    if [ ! -f "$src" ]; then
        echo "  MISSING: [$src_rel] (in $base)"
        ((errors++)) || true
        continue
    fi

    if [ -f "$out" ] && [ "$FORCE" = false ]; then
        ((skipped++)) || true
        continue
    fi

    mkdir -p "$(dirname "$out")"

    if ffmpeg -y -i "$src" -vf "scale=${sz}:${sz}:flags=lanczos" "$out" </dev/null 2>/dev/null; then
        echo "  OK: $out_rel  (${sz}x${sz})"
        ((generated++)) || true
    else
        echo "  FAIL: $out_rel"
        ((errors++)) || true
    fi
done

echo ""
echo "Done."
