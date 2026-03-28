#!/bin/bash
# Assembles BOAM user-facing documentation into Quartz content directory.
# Called by the CI workflow after cloning Quartz.
# $1 = repo root, $2 = quartz content dir

set -euo pipefail

REPO_ROOT="${1:?Usage: build-content.sh <repo-root> <content-dir>}"
CONTENT_DIR="${2:?Usage: build-content.sh <repo-root> <content-dir>}"

rm -rf "$CONTENT_DIR"
mkdir -p "$CONTENT_DIR/docs/features"

# Landing page
cp "$REPO_ROOT/README.md" "$CONTENT_DIR/index.md"

# User-facing feature docs — copy with numeric prefixes for ordered navigation.
# The order field from frontmatter is used as the prefix.
copy_with_order() {
    local src_dir="$1" dst_dir="$2"
    find "$src_dir" -maxdepth 1 -name '*.md' | while read -r f; do
        local base=$(basename "$f")
        # Read order from frontmatter (line: "order: N")
        local order=$(grep -m1 '^order:' "$f" | sed 's/order: *//')
        if [ -n "$order" ]; then
            local padded=$(printf "%02d" "$order")
            cp "$f" "$dst_dir/${padded}_${base}"
        else
            cp "$f" "$dst_dir/$base"
        fi
    done
}

# Behaviours subfolder — prefixed 04b to sort between Behaviour (04) and Heatmaps (05)
mkdir -p "$CONTENT_DIR/docs/features/04b_behaviours"
copy_with_order "$REPO_ROOT/docs/features" "$CONTENT_DIR/docs/features"
copy_with_order "$REPO_ROOT/docs/features/behaviours" "$CONTENT_DIR/docs/features/04b_behaviours"

# Fix links in copied files: behaviours/ → 04b_behaviours/
find "$CONTENT_DIR" -name '*.md' -exec sed -i 's|behaviours/|04b_behaviours/|g' {} \;

# Documentation images
if [ -d "$REPO_ROOT/docs/images" ]; then
    mkdir -p "$CONTENT_DIR/docs/images"
    cp "$REPO_ROOT/docs/images/"* "$CONTENT_DIR/docs/images/"
fi

echo "Content assembled: $(find "$CONTENT_DIR" -name '*.md' | wc -l) pages"
