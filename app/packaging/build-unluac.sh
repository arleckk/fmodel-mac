#!/usr/bin/env bash
# Builds FModel's unluac (Lua 5.0–5.4 decompiler, Java, MIT) as a macOS arm64 shared library with GraalVM
# Native Image, for bundling inside FModelMac.app. FModel only publishes unluac.dll (Windows) and unluac.so (Linux).
#
# Output: vendor/unluac-build/libunluac.dylib (exports unluac_create_isolate, unluac_tear_down_isolate,
#         unluac_decompile_buffer, unluac_free: what CUE4Parse's Unluac class binds)
#
# Sources (pinned) go to vendor/unluac-src/, GraalVM CE to vendor/graalvm/ (both reused on later runs).
#
# Usage:  app/packaging/build-unluac.sh [--clean]
#   --clean   wipe vendor/unluac-build/ before building (sources and GraalVM are kept)
#
# Requirements: Xcode command line tools (clang, codesign), git, curl, tar, shasum. GraalVM is downloaded
# (~340 MB) the first time; nothing is installed system-wide.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
SRC="${ROOT}/vendor/unluac-src"
BUILD="${ROOT}/vendor/unluac-build"
GRAAL_DIR="${ROOT}/vendor/graalvm"
GRAAL_DL="${ROOT}/vendor/graalvm-dl"

UNLUAC_REPO="https://github.com/FModel/unluac.git"
UNLUAC_BRANCH="native"
UNLUAC_COMMIT="fe187b4"  # "Add NativeExports", the source of FModel's unluac v1.0.0

GRAAL_TAG="graal-25.4.4.1.1"
GRAAL_FILE="graalvm-community-jdk-25i4-25.0.4.1.1_macos-aarch64_bin.tar.gz"
GRAAL_URL="https://github.com/graalvm/graalvm-ce-builds/releases/download/${GRAAL_TAG}/${GRAAL_FILE}"
GRAAL_SHA256="4ec9932aa4aab1afca0ea31982d32d6f956615c61fe21a227b49756b25adda4f"

[[ "${1:-}" == "--clean" ]] && rm -rf "$BUILD"

# GraalVM CE (checksum from the release's .sha256)
if ! ls "$GRAAL_DIR"/*/Contents/Home/bin/native-image >/dev/null 2>&1; then
    mkdir -p "$GRAAL_DL" "$GRAAL_DIR"
    if [[ ! -f "$GRAAL_DL/graalvm.tar.gz" ]]; then
        echo "==> Downloading GraalVM CE ($GRAAL_TAG)"
        curl -fL --progress-bar -o "$GRAAL_DL/graalvm.tar.gz" "$GRAAL_URL"
    fi
    echo "$GRAAL_SHA256  $GRAAL_DL/graalvm.tar.gz" | shasum -a 256 -c -
    tar -xzf "$GRAAL_DL/graalvm.tar.gz" -C "$GRAAL_DIR"
fi
GRAAL_HOME="$(ls -d "$GRAAL_DIR"/*/Contents/Home | head -1)"

# unluac sources at the pinned commit
if [[ ! -d "$SRC/.git" ]]; then
    echo "==> Cloning unluac"
    git clone -q -b "$UNLUAC_BRANCH" "$UNLUAC_REPO" "$SRC"
fi
if ! git -C "$SRC" cat-file -e "${UNLUAC_COMMIT}^{commit}" 2>/dev/null; then
    git -C "$SRC" fetch -q --unshallow 2>/dev/null || git -C "$SRC" fetch -q origin "$UNLUAC_BRANCH"
fi
git -C "$SRC" -c advice.detachedHead=false checkout -q "$UNLUAC_COMMIT"

echo "==> Compiling unluac (javac)"
rm -rf "$BUILD/classes"
mkdir -p "$BUILD/classes"
find "$SRC/src" -name '*.java' > "$BUILD/sources.txt"
"$GRAAL_HOME/bin/javac" -nowarn -encoding UTF-8 -d "$BUILD/classes" @"$BUILD/sources.txt"

echo "==> Building libunluac.dylib (native-image --shared)"
(
    cd "$BUILD"
    "$GRAAL_HOME/bin/native-image" --shared --no-fallback -O2 -march=armv8-a \
        -cp classes -o libunluac -H:+UnlockExperimentalVMOptions -H:-CheckToolchain >/dev/null
)
install_name_tool -id "@rpath/libunluac.dylib" "$BUILD/libunluac.dylib"
codesign --force -s - "$BUILD/libunluac.dylib" >/dev/null 2>&1
cp "$SRC/license.txt" "$BUILD/LICENSE-unluac.txt"

for symbol in unluac_create_isolate unluac_tear_down_isolate unluac_decompile_buffer unluac_free; do
    nm -gU "$BUILD/libunluac.dylib" | grep -q " _${symbol}$" || { echo "error: $symbol not exported" >&2; exit 1; }
done
echo "==> Done: $BUILD/libunluac.dylib ($(du -h "$BUILD/libunluac.dylib" | cut -f1))"
otool -L "$BUILD/libunluac.dylib" | tail -n +2
