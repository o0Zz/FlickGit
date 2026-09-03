#!/usr/bin/env bash
#
# Assembles FlickGit.app from the published .NET output and the compiled Finder Sync extension.
#
# There is no Xcode project anywhere in this: the app is a directory with a known layout, and
# assembling it in a script keeps the whole thing readable and diffable. What the script must get
# right is the *order* — the extension is signed before the app that contains it, because signing
# an app seals its contents and a later signature inside would break the seal.
#
# Usage: bundle.sh <version> <arm64-publish-dir> <x64-publish-dir> <output-dir>
set -euo pipefail

VERSION="${1:?version}"
ARM64_DIR="${2:?arm64 publish dir}"
X64_DIR="${3:?x64 publish dir}"
OUT="${4:?output dir}"

APP="$OUT/FlickGit.app"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FINDER_SRC="$HERE/../FlickGit.FinderSync"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$APP/Contents/PlugIns"

# ---- the .NET payload -------------------------------------------------------------------------
#
# Both architectures are published and merged with lipo where a file is a Mach-O binary, so one
# bundle runs natively on Apple silicon and on Intel. Everything else (the managed assemblies, which
# are architecture-neutral IL) is copied once.
cp -R "$ARM64_DIR/." "$APP/Contents/MacOS/"

merged=0
while IFS= read -r -d '' file; do
    rel="${file#"$ARM64_DIR"/}"
    other="$X64_DIR/$rel"

    [ -f "$other" ] || continue

    # Only real Mach-O files can be merged; a managed .dll is IL and the same for both.
    if file -b "$file" | grep -q "Mach-O"; then
        lipo -create "$file" "$other" -output "$APP/Contents/MacOS/$rel" 2>/dev/null && merged=$((merged + 1)) || true
    fi
done < <(find "$ARM64_DIR" -type f -print0)

echo "lipo merged $merged binaries"

# ---- Info.plist -------------------------------------------------------------------------------
sed "s/__VERSION__/$VERSION/g" "$HERE/Info.plist" > "$APP/Contents/Info.plist"

# ---- the Finder Sync extension ----------------------------------------------------------------
#
# swiftc rather than xcodebuild: there is no .xcodeproj to drive, and an app extension is a bundle
# with a known layout like the app itself. Built for both architectures and merged, for the same
# reason the payload is.
APPEX="$APP/Contents/PlugIns/FlickGitFinder.appex"
mkdir -p "$APPEX/Contents/MacOS"

for arch in arm64 x86_64; do
    swiftc \
        -target "$arch-apple-macos12.0" \
        -module-name FlickGitFinder \
        -framework Cocoa -framework FinderSync \
        -O \
        -o "$OUT/FlickGitFinder.$arch" \
        "$FINDER_SRC/FinderSync.swift"
done

lipo -create "$OUT/FlickGitFinder.arm64" "$OUT/FlickGitFinder.x86_64" \
    -output "$APPEX/Contents/MacOS/FlickGitFinder"
rm -f "$OUT/FlickGitFinder.arm64" "$OUT/FlickGitFinder.x86_64"

sed "s|<string>0.0.0</string>|<string>$VERSION</string>|g" "$FINDER_SRC/Info.plist" \
    > "$APPEX/Contents/Info.plist"

# ---- signing ----------------------------------------------------------------------------------
#
# Ad-hoc (`-s -`) unless a real identity is in the environment. Ad-hoc is enough to prove the bundle
# is well formed and that `codesign --verify` accepts its structure, which is what CI can check; it
# is *not* enough for the extension to load on someone else's machine, because Finder requires a
# notarised signature for that. That gate needs a Developer ID and cannot be crossed here.
IDENTITY="${MACOS_SIGN_IDENTITY:--}"

codesign --force --sign "$IDENTITY" --timestamp=none "$APPEX"
codesign --force --sign "$IDENTITY" --timestamp=none --deep "$APP"

codesign --verify --deep --strict "$APP"

echo "built $APP"
