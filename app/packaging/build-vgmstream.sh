#!/usr/bin/env bash
# Builds a self-contained vgmstream-cli (macOS arm64) for bundling inside FModel.app.
#
# Output: vendor/vgmstream-build/vgmstream-cli
#   - links only against macOS system libraries (/usr/lib, /System/Library)
#   - all codec libraries are statically linked
#   - FFmpeg is a minimal LGPL-2.1+ build (decoders/demuxers/parsers only, no encoders,
#     no GPL/nonfree code, no external/autodetected libraries)
#
# Sources (pinned) are downloaded into vendor/vgmstream-src/ and reused on later runs
# (that folder and vendor/vgmstream-build/ act as the build cache).
#
# Usage:  app/packaging/build-vgmstream.sh [--clean]
#   --clean   wipe vendor/vgmstream-build/ before building (sources are kept)
#
# Requirements: Xcode command line tools (clang, make, strip, codesign), cmake >= 3.18,
#               git, curl, tar, shasum. Nothing from Homebrew is linked.
set -euo pipefail

# --------------------------------------------------------------------------------------
# Configuration / pinned versions
# --------------------------------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
SRC="${ROOT}/vendor/vgmstream-src"
BUILD="${ROOT}/vendor/vgmstream-build"
DL="${SRC}/downloads"

JOBS="${JOBS:-12}"
ARCH="arm64"
export MACOSX_DEPLOYMENT_TARGET="${MACOSX_DEPLOYMENT_TARGET:-12.0}"

VGMSTREAM_REPO="https://github.com/vgmstream/vgmstream.git"
VGMSTREAM_TAG="r2117"

# The versions/commits below are the ones vgmstream r2117 pins in cmake/dependencies/*.cmake
FFMPEG_VER="7.1.1"   # vgmstream ffmpeg.cmake: GIT_TAG n7.1.1
FFMPEG_URL="https://ffmpeg.org/releases/ffmpeg-${FFMPEG_VER}.tar.xz"
FFMPEG_SHA256="733984395e0dbbe5c046abda2dc49a5544e7e0e1e2366bba849222ae9e3a03b1"

MPG123_VER="1.31.1"  # vgmstream mpg123.cmake
MPG123_URL="https://downloads.sourceforge.net/mpg123/mpg123-${MPG123_VER}.tar.bz2"
MPG123_SHA256="5dcb0936efd44cb583498b6585845206f002a7b19d5066a2683be361954d955a"

SPEEX_VER="1.2.1"    # vgmstream speex.cmake: GIT_TAG Speex-1.2.1 (release tarball has a ready configure)
SPEEX_URL="https://downloads.xiph.org/releases/speex/speex-${SPEEX_VER}.tar.gz"
SPEEX_SHA256="4b44d4f2b38a370a2d98a78329fefc56a0cf93d1c1be70029217baae6628feea"

CELT0061_URL="https://downloads.xiph.org/releases/celt/celt-0.6.1.tar.gz"
CELT0061_SHA256="a991dff4a9e0772ede0881d81cdc7ac559148c2194885cbdd534fe4af43779da"
CELT0110_URL="https://downloads.xiph.org/releases/celt/celt-0.11.0.tar.gz"
CELT0110_SHA256="c94d4d34f5a2caa1574b1a94869202cacd959b55f643a8bafe0660008acad9c3"

OGG_REPO="https://gitlab.xiph.org/xiph/ogg"
OGG_COMMIT="fa80aae9d50096160f2b56ada35527d7aee3f746"
VORBIS_REPO="https://gitlab.xiph.org/xiph/vorbis"
VORBIS_COMMIT="2eac96b03ff67953354cb0a649c08aa3a23267ef"
ATRAC9_REPO="https://github.com/Thealexbarney/LibAtrac9"
ATRAC9_COMMIT="6a9e00f6c7abd74d037fd210b6670d3cdb313049"

# FFmpeg component lists: vgmstream's curated list (cmake/dependencies/ffmpeg.cmake and
# ext_libs/extra/ffmpeg_options.txt), with the native "opus" decoder instead of libopus.
FFMPEG_DEMUXERS="ac3,eac3,spdif,asf,xwma,mov,oma,xmv,ogg,flac,wav,aac,mp3,smacker,bink,binka,caf,mpc,mpc8,tak,ape"
FFMPEG_PARSERS="ac3,mpegaudio,xma,vorbis,opus"
FFMPEG_DECODERS="ac3,eac3,wmapro,wmav1,wmav2,xma1,xma2,aac,atrac3,atrac3p,mp2float,mp3float,smackaud,binkaudio_dct,binkaudio_rdft,pcm_s16be,pcm_s16be_planar,pcm_s16le,pcm_s16le_planar,pcm_s8,pcm_s8_planar,flac,vorbis,opus,mpc7,mpc8,alac,adpcm_ima_qt,adpcm_ima_dk3,adpcm_ima_dk4,tak,ape"

# --------------------------------------------------------------------------------------
# Helpers
# --------------------------------------------------------------------------------------
log() { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
die() { printf '\033[1;31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

need() { command -v "$1" >/dev/null 2>&1 || die "'$1' not found in PATH"; }

fetch_tarball() { # url sha256 -> path of verified tarball in $DL
    local url="$1" sha="$2" file
    file="${DL}/$(basename "$url")"
    mkdir -p "$DL"
    if [[ ! -f "$file" ]]; then
        log "Downloading $(basename "$url")"
        curl -fL --retry 3 -sS -o "${file}.part" "$url"
        mv "${file}.part" "$file"
    fi
    local got
    got="$(shasum -a 256 "$file" | awk '{print $1}')"
    [[ "$got" == "$sha" ]] || die "SHA-256 mismatch for $file (got $got, expected $sha)"
    printf '%s' "$file"
}

extract_tarball() { # tarball dest_dir_name (top-level dir inside tarball)
    local tarball="$1" dir="$2"
    if [[ ! -d "${SRC}/${dir}" ]]; then
        log "Extracting $(basename "$tarball")"
        tar -xf "$tarball" -C "$SRC"
        [[ -d "${SRC}/${dir}" ]] || die "expected ${SRC}/${dir} after extracting $tarball"
    fi
}

git_at_commit() { # dir repo ref(commit or tag)
    local dir="${SRC}/$1" repo="$2" ref="$3"
    if [[ -d "${dir}/.git" ]] && [[ "$(git -C "$dir" rev-parse HEAD 2>/dev/null)" == "$(git -C "$dir" rev-parse "${ref}^{commit}" 2>/dev/null)" ]]; then
        return 0
    fi
    log "Fetching $1 @ ${ref}"
    rm -rf "$dir"
    mkdir -p "$dir"
    git -C "$dir" init -q
    git -C "$dir" remote add origin "$repo"
    git -C "$dir" fetch -q --depth 1 origin "$ref"
    git -C "$dir" -c advice.detachedHead=false checkout -q FETCH_HEAD
}

# --------------------------------------------------------------------------------------
# Pre-flight
# --------------------------------------------------------------------------------------
[[ "$(uname -s)" == "Darwin" ]] || die "macOS only"
for t in cmake make clang git curl tar shasum strip codesign otool pkg-config xcrun; do need "$t"; done

if [[ "${1:-}" == "--clean" ]]; then
    log "Cleaning ${BUILD}"
    rm -rf "$BUILD"
fi
mkdir -p "$SRC" "$BUILD"

# Isolate from Homebrew / user environment: system clang, no extra include/lib paths,
# pkg-config only sees our own FFmpeg build.
export CC="/usr/bin/clang"
export CXX="/usr/bin/clang++"
export AR="$(xcrun -f ar)"
export RANLIB="$(xcrun -f ranlib)"
export SDKROOT="$(xcrun --sdk macosx --show-sdk-path)"
unset CPATH C_INCLUDE_PATH CPLUS_INCLUDE_PATH LIBRARY_PATH DYLD_LIBRARY_PATH \
      CFLAGS CXXFLAGS CPPFLAGS LDFLAGS OBJCFLAGS PKG_CONFIG_PATH 2>/dev/null || true
FFMPEG_PREFIX="${BUILD}/ffmpeg-prefix"
export PKG_CONFIG_LIBDIR="${FFMPEG_PREFIX}/lib/pkgconfig"

ARCH_FLAGS="-arch ${ARCH} -mmacosx-version-min=${MACOSX_DEPLOYMENT_TARGET}"

# --------------------------------------------------------------------------------------
# Sources
# --------------------------------------------------------------------------------------
log "Preparing sources in ${SRC}"
if [[ ! -d "${SRC}/vgmstream/.git" ]]; then
    log "Cloning vgmstream ${VGMSTREAM_TAG}"
    git clone -q --depth 1 --branch "$VGMSTREAM_TAG" -c advice.detachedHead=false \
        "$VGMSTREAM_REPO" "${SRC}/vgmstream"
fi
[[ "$(git -C "${SRC}/vgmstream" describe --tags --exact-match 2>/dev/null)" == "$VGMSTREAM_TAG" ]] \
    || die "${SRC}/vgmstream is not at tag ${VGMSTREAM_TAG}"

extract_tarball "$(fetch_tarball "$FFMPEG_URL" "$FFMPEG_SHA256")"     "ffmpeg-${FFMPEG_VER}"
extract_tarball "$(fetch_tarball "$MPG123_URL" "$MPG123_SHA256")"     "mpg123-${MPG123_VER}"
extract_tarball "$(fetch_tarball "$SPEEX_URL" "$SPEEX_SHA256")"       "speex-${SPEEX_VER}"
extract_tarball "$(fetch_tarball "$CELT0061_URL" "$CELT0061_SHA256")" "celt-0.6.1"
extract_tarball "$(fetch_tarball "$CELT0110_URL" "$CELT0110_SHA256")" "celt-0.11.0"
git_at_commit ogg       "$OGG_REPO"    "$OGG_COMMIT"
git_at_commit vorbis    "$VORBIS_REPO" "$VORBIS_COMMIT"
git_at_commit LibAtrac9 "$ATRAC9_REPO" "$ATRAC9_COMMIT"

# CELT's 2009/2011 config.guess/config.sub predate Apple Silicon, and mpg123's reports
# "arm-apple-darwin" (=> generic C decoder instead of NEON64). vgmstream ships recent
# copies for exactly this purpose (its CMake only applies them for Linux aarch64).
for d in celt-0.6.1 celt-0.11.0 mpg123-${MPG123_VER}/build; do
    cp -f "${SRC}/vgmstream/cmake/autoconf/config.guess" "${SRC}/${d}/config.guess"
    cp -f "${SRC}/vgmstream/cmake/autoconf/config.sub"   "${SRC}/${d}/config.sub"
done

# --------------------------------------------------------------------------------------
# FFmpeg (minimal static LGPL build)
# --------------------------------------------------------------------------------------
FFMPEG_BUILD="${BUILD}/ffmpeg"
if [[ ! -f "${FFMPEG_PREFIX}/lib/libavformat.a" ]]; then
    log "Configuring FFmpeg ${FFMPEG_VER} (static, LGPL, decode-only)"
    rm -rf "$FFMPEG_BUILD" "$FFMPEG_PREFIX"
    mkdir -p "$FFMPEG_BUILD"
    (
        cd "$FFMPEG_BUILD"
        "${SRC}/ffmpeg-${FFMPEG_VER}/configure" \
            --prefix="$FFMPEG_PREFIX" \
            --cc="$CC" --ar="$AR" --ranlib="$RANLIB" \
            --arch=aarch64 --target-os=darwin \
            --enable-static --disable-shared \
            --disable-gpl --disable-nonfree --disable-version3 \
            --disable-programs --disable-doc --disable-network --disable-autodetect \
            --disable-avdevice --disable-swscale --disable-postproc --disable-avfilter \
            --disable-everything \
            --disable-encoders --disable-muxers --disable-protocols --disable-bsfs \
            --disable-indevs --disable-outdevs --disable-filters --disable-hwaccels \
            --disable-iconv --disable-zlib --disable-bzlib --disable-lzma \
            --disable-securetransport --disable-audiotoolbox --disable-videotoolbox \
            --disable-sdl2 --disable-debug \
            --enable-swresample \
            --enable-demuxer="$FFMPEG_DEMUXERS" \
            --enable-parser="$FFMPEG_PARSERS" \
            --enable-decoder="$FFMPEG_DECODERS" \
            --extra-cflags="$ARCH_FLAGS" \
            --extra-ldflags="$ARCH_FLAGS" \
            --pkg-config=false
    )
    grep -q '^#define CONFIG_GPL 0' "${FFMPEG_BUILD}/config.h"      || die "FFmpeg configured with GPL"
    grep -q '^#define CONFIG_NONFREE 0' "${FFMPEG_BUILD}/config.h"  || die "FFmpeg configured with nonfree"
    grep -q '^#define CONFIG_VERSION3 0' "${FFMPEG_BUILD}/config.h" || die "FFmpeg configured with (L)GPLv3"
    log "Building FFmpeg"
    make -C "$FFMPEG_BUILD" -j "$JOBS"
    make -C "$FFMPEG_BUILD" install
fi
grep -h '^#define FFMPEG_LICENSE' "${FFMPEG_BUILD}/config.h" || true

# --------------------------------------------------------------------------------------
# vgmstream (its CMake builds mpg123, ogg, vorbis, speex, LibAtrac9, CELT from the
# local source trees passed via *_PATH; FFmpeg is our prebuilt static build found via
# CMAKE_PREFIX_PATH + pkg-config, which only see ${FFMPEG_PREFIX}).
# Git/Subversion lookups are disabled so vgmstream's FetchDependency can never silently
# download anything: a dependency that isn't provided locally gets turned OFF instead
# (and the checks below fail the build).
# --------------------------------------------------------------------------------------
VGM_BUILD="${BUILD}/cmake"
log "Configuring vgmstream ${VGMSTREAM_TAG}"
cmake -S "${SRC}/vgmstream" -B "$VGM_BUILD" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_C_COMPILER="$CC" -DCMAKE_CXX_COMPILER="$CXX" \
    -DCMAKE_OSX_ARCHITECTURES="$ARCH" \
    -DCMAKE_OSX_DEPLOYMENT_TARGET="$MACOSX_DEPLOYMENT_TARGET" \
    -DCMAKE_OSX_SYSROOT="$SDKROOT" \
    -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
    -DCMAKE_IGNORE_PREFIX_PATH="/opt/homebrew;/usr/local;/opt/local" \
    -DCMAKE_FIND_FRAMEWORK=NEVER \
    -DCMAKE_PREFIX_PATH="$FFMPEG_PREFIX" \
    -DCMAKE_DISABLE_FIND_PACKAGE_Git=ON -DCMAKE_DISABLE_FIND_PACKAGE_Subversion=ON \
    -DBUILD_TESTING=OFF \
    -DBUILD_CLI=ON -DBUILD_V123=OFF -DBUILD_AUDACIOUS=OFF \
    -DBUILD_STATIC=OFF -DBUILD_SHARED_LIBS=OFF \
    -DUSE_FFMPEG=ON \
    -DUSE_MPEG=ON    -DMPEG_PATH="${SRC}/mpg123-${MPG123_VER}" \
    -DUSE_VORBIS=ON  -DOGG_PATH="${SRC}/ogg" -DVORBIS_PATH="${SRC}/vorbis" \
    -DUSE_SPEEX=ON   -DSPEEX_PATH="${SRC}/speex-${SPEEX_VER}" \
    -DUSE_ATRAC9=ON  -DATRAC9_PATH="${SRC}/LibAtrac9" \
    -DUSE_CELT=ON    -DCELT_0061_PATH="${SRC}/celt-0.6.1" -DCELT_0110_PATH="${SRC}/celt-0.11.0" \
    -DUSE_G7221=ON \
    -DUSE_G719=OFF

# FFmpeg must be our static build (not a system/Homebrew one)
grep -q "^FFMPEG_LIBRARIES:STRING=${FFMPEG_PREFIX}/lib/" "${VGM_BUILD}/CMakeCache.txt" \
    || die "CMake did not pick the FFmpeg build in ${FFMPEG_PREFIX}"

log "Building vgmstream-cli"
cmake --build "$VGM_BUILD" --target vgmstream_cli -j "$JOBS"

# --------------------------------------------------------------------------------------
# Finalize: strip, sign, verify
# --------------------------------------------------------------------------------------
OUT="${BUILD}/vgmstream-cli"
cp -f "${VGM_BUILD}/cli/vgmstream-cli" "$OUT"
chmod 755 "$OUT"

# Sanity: codec libraries really linked in (checked before stripping).
# (nm output is captured first: "nm | grep -q" would SIGPIPE nm and trip pipefail)
SYMS="$(nm "$OUT")"
for sym in _avformat_open_input _avcodec_send_packet _mpg123_init _ov_open_callbacks _vorbis_synthesis \
           _speex_decoder_init _Atrac9InitDecoder _celt_decoder_create_0061 _celt_decoder_create_0110; do
    grep -q " T ${sym}$" <<<"$SYMS" || die "symbol ${sym} missing from vgmstream-cli"
done

strip "$OUT"
codesign --force --sign - "$OUT"

log "Verifying linkage"
otool -L "$OUT"
bad="$(otool -L "$OUT" | tail -n +2 | awk '{print $1}' | grep -v -E '^(/usr/lib/|/System/Library/)' || true)"
[[ -z "$bad" ]] || die "non-system dylibs linked: ${bad}"
[[ "$(lipo -archs "$OUT")" == "$ARCH" ]] || die "unexpected architecture: $(lipo -archs "$OUT")"
codesign --verify --strict "$OUT"

log "Done: ${OUT} ($(du -h "$OUT" | awk '{print $1}'))"
"$OUT" -V >/dev/null 2>&1 || true   # -V prints JSON and exits 1 by design
