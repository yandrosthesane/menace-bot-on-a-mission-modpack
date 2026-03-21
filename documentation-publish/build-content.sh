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

# User-facing feature docs
for f in "$REPO_ROOT/docs/features/"*.md; do
    [ -f "$f" ] && cp "$f" "$CONTENT_DIR/docs/features/"
done

echo "Content assembled: $(find "$CONTENT_DIR" -name '*.md' | wc -l) pages"
