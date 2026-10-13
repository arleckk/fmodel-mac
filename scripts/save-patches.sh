#!/bin/bash
# Saves the local changes to upstream/CUE4Parse into patches/CUE4Parse.patch (run it after editing CUE4Parse).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CUE4PARSE="$ROOT/upstream/CUE4Parse"

if [[ -n "$(git -C "$CUE4PARSE" ls-files --others --exclude-standard)" ]]; then
    echo "note: new files in CUE4Parse are included with git add -N first:" >&2
    git -C "$CUE4PARSE" ls-files --others --exclude-standard >&2
    git -C "$CUE4PARSE" add -N .
fi
git -C "$CUE4PARSE" diff --full-index > "$ROOT/patches/CUE4Parse.patch"
echo "patches/CUE4Parse.patch: $(git -C "$CUE4PARSE" diff --stat | tail -1)"
