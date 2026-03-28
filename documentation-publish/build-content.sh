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

# User-facing feature docs (recursive — includes subdirectories like behaviours/)
find "$REPO_ROOT/docs/features" -name '*.md' | while read -r f; do
    rel="${f#$REPO_ROOT/docs/features/}"
    mkdir -p "$CONTENT_DIR/docs/features/$(dirname "$rel")"
    cp "$f" "$CONTENT_DIR/docs/features/$rel"
done

# Documentation images
if [ -d "$REPO_ROOT/docs/images" ]; then
    mkdir -p "$CONTENT_DIR/docs/images"
    cp "$REPO_ROOT/docs/images/"* "$CONTENT_DIR/docs/images/"
fi

echo "Content assembled: $(find "$CONTENT_DIR" -name '*.md' | wc -l) pages"
