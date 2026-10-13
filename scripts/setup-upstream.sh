#!/bin/bash
# Recreates the folders this repo doesn't track, at the exact versions FModel was built against:
#   upstream/   4sval/FModel (with the CUE4Parse submodule) + patches/CUE4Parse.patch
#   vendor/     vgmstream, via app/packaging/build-vgmstream.sh (optional: --with-vgmstream)
# Safe to run again: existing checkouts are reused and the patch is only applied once.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FMODEL_REPO="https://github.com/4sval/FModel.git"
FMODEL_COMMIT="d61549776a0ca9411903804640c7a61de721c4e6"
CUE4PARSE_COMMIT="3fc091a085a609bdb7bd2f8934890051b88ac18b"

with_vgmstream=false
[[ "${1:-}" == "--with-vgmstream" ]] && with_vgmstream=true

step() { printf '\033[1;34m==> %s\033[0m\n' "$*"; }

# A shallow checkout of one commit
checkout() { # repo commit dir
    local repo="$1" commit="$2" dir="$3"
    if [[ -d "$dir/.git" ]]; then
        [[ "$(git -C "$dir" rev-parse HEAD)" == "$commit" ]] && return
        git -C "$dir" fetch -q --depth 1 origin "$commit"
    else
        git init -q "$dir"
        git -C "$dir" remote add origin "$repo"
        git -C "$dir" fetch -q --depth 1 origin "$commit"
    fi
    git -C "$dir" -c advice.detachedHead=false checkout -q "$commit"
}

step "FModel $FMODEL_COMMIT -> upstream/"
checkout "$FMODEL_REPO" "$FMODEL_COMMIT" "$ROOT/upstream"
git -C "$ROOT/upstream" submodule update --init --recursive --depth 1
[[ "$(git -C "$ROOT/upstream/CUE4Parse" rev-parse HEAD)" == "$CUE4PARSE_COMMIT" ]] \
    || { echo "error: CUE4Parse isn't at $CUE4PARSE_COMMIT" >&2; exit 1; }

step "Patching CUE4Parse"
PATCH="$ROOT/patches/CUE4Parse.patch"
if git -C "$ROOT/upstream/CUE4Parse" apply --reverse --check "$PATCH" 2>/dev/null; then
    echo "already applied"
else
    git -C "$ROOT/upstream/CUE4Parse" apply "$PATCH"
fi

if $with_vgmstream; then
    step "vgmstream"
    "$ROOT/app/packaging/build-vgmstream.sh"
fi

step "Done. Build the app with app/packaging/build-app.sh"
