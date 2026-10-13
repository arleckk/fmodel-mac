#!/bin/bash
# Builds dist/FModelMac.app: self-contained .NET publish for osx-arm64, icon, Info.plist, ad-hoc signature.
# Usage: app/packaging/build-app.sh   (no .NET needed on the machine that runs the resulting app)
set -euo pipefail

PACKAGING_DIR="$(cd "$(dirname "$0")" && pwd)"
APP_ROOT="$(dirname "$PACKAGING_DIR")"
PROJECT="$APP_ROOT/FModelMac/FModelMac.csproj"
RID="osx-arm64"
APP_NAME="FModelMac"
BUNDLE_ID="local.fmodelmac"
DIST="$APP_ROOT/dist"
PUBLISH_DIR="$DIST/publish-$RID"
BUNDLE="$DIST/$APP_NAME.app"

export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | head -1)"
VERSION="${VERSION:-0.0.0}"

echo "==> Publishing $APP_NAME $VERSION ($RID, self-contained)"
rm -rf "$PUBLISH_DIR"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
    -p:PublishReadyToRun=true -p:DebugType=none -o "$PUBLISH_DIR" -v quiet -nologo

for lib in CUE4Parse-Natives.dylib liboodle-data-shared.dylib libAvaloniaNative.dylib libSkiaSharp.dylib; do
    if [[ ! -f "$PUBLISH_DIR/$lib" ]]; then
        echo "error: $lib missing from publish output" >&2
        exit 1
    fi
done

# vgmstream (Wwise, Bink, Opus… audio) ships inside the app when packaging/build-vgmstream.sh was run
if [[ -f "$PUBLISH_DIR/vgmstream-cli" ]]; then
    chmod +x "$PUBLISH_DIR/vgmstream-cli"
else
    echo "warning: vgmstream-cli not bundled (run packaging/build-vgmstream.sh); audio will need Homebrew's vgmstream" >&2
fi

# unluac (compiled Lua) ships inside the app when packaging/build-unluac.sh was run
if [[ ! -f "$PUBLISH_DIR/libunluac.dylib" ]]; then
    echo "warning: libunluac.dylib not bundled (run packaging/build-unluac.sh); Lua chunks won't be decompiled" >&2
fi

echo "==> Assembling $BUNDLE"
rm -rf "$BUNDLE"
mkdir -p "$BUNDLE/Contents/MacOS" "$BUNDLE/Contents/Resources"
cp -R "$PUBLISH_DIR/." "$BUNDLE/Contents/MacOS/"
cp "$PACKAGING_DIR/$APP_NAME.icns" "$BUNDLE/Contents/Resources/$APP_NAME.icns"

# unluac is MIT: its notice ships with it
UNLUAC_LICENSE="$APP_ROOT/../vendor/unluac-build/LICENSE-unluac.txt"
if [[ -f "$BUNDLE/Contents/MacOS/libunluac.dylib" && -f "$UNLUAC_LICENSE" ]]; then
    mkdir -p "$BUNDLE/Contents/Resources/Licenses/unluac"
    cp "$UNLUAC_LICENSE" "$BUNDLE/Contents/Resources/Licenses/unluac/LICENSE.txt"
fi

# Licenses of what vgmstream-cli links statically (FFmpeg and mpg123 are LGPL: their notices must ship with it)
VGM_SRC="$APP_ROOT/../vendor/vgmstream-src"
if [[ -f "$BUNDLE/Contents/MacOS/vgmstream-cli" && -d "$VGM_SRC" ]]; then
    LICENSES="$BUNDLE/Contents/Resources/Licenses/vgmstream"
    mkdir -p "$LICENSES"
    for entry in "vgmstream/COPYING:vgmstream.txt" "ffmpeg-7.1.1/COPYING.LGPLv2.1:FFmpeg-LGPL-2.1.txt" \
                 "mpg123-1.31.1/COPYING:mpg123.txt" "ogg/COPYING:libogg.txt" "vorbis/COPYING:libvorbis.txt" \
                 "speex-1.2.1/COPYING:speex.txt" "LibAtrac9/LICENSE:LibAtrac9.txt" \
                 "celt-0.6.1/COPYING:celt-0.6.1.txt" "celt-0.11.0/COPYING:celt-0.11.0.txt"; do
        src="$VGM_SRC/${entry%%:*}"
        [[ -f "$src" ]] && cp "$src" "$LICENSES/${entry##*:}"
    done
    cat > "$LICENSES/README.txt" <<'NOTICE'
vgmstream-cli (r2117) is built by app/packaging/build-vgmstream.sh from the official sources of vgmstream,
FFmpeg 7.1.1 (LGPL-2.1+, decoders only, no GPL or non-free parts), mpg123 1.31.1 (LGPL-2.1), libogg, libvorbis,
speex, LibAtrac9 and CELT, all linked statically. The script pins every source version and rebuilds the binary;
to relink with other versions of the LGPL libraries, edit the versions in it and run it again.
NOTICE
fi

cat > "$BUNDLE/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleExecutable</key>
    <string>$APP_NAME</string>
    <key>CFBundleIconFile</key>
    <string>$APP_NAME</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.developer-tools</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSHumanReadableCopyright</key>
    <string>Based on FModel and CUE4Parse (GPL-3.0 / Apache-2.0)</string>
</dict>
</plist>
PLIST
plutil -lint "$BUNDLE/Contents/Info.plist" >/dev/null

echo "==> Signing (ad-hoc)"
# Apple Silicon refuses to run unsigned Mach-O code; an ad-hoc signature is enough for local use
codesign --force --deep --sign - "$BUNDLE"
codesign --verify --deep --strict "$BUNDLE"

# Refresh Finder/Dock icon caches for this bundle
touch "$BUNDLE"

SIZE="$(du -sh "$BUNDLE" | cut -f1)"
echo "==> Done: $BUNDLE ($SIZE)"
