#!/usr/bin/env bash
# Builds Keystone.app for this Mac and, with --install, copies it to /Applications.
# Usage: scripts/package-mac.sh [--install]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/src/Keystone/Keystone.csproj"
OUT="$ROOT/artifacts"
APP="$OUT/Keystone.app"
VERSION="${VERSION:-$(sed -n "s:.*<Version>\(.*\)</Version>.*:\1:p" "$PROJECT")}"

case "$(uname -m)" in
    arm64) RID=osx-arm64 ;;
    *) RID=osx-x64 ;;
esac

echo "Publishing for $RID..."
rm -rf "$OUT/publish" "$APP" "$OUT/Runway.app"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
    -p:UseAppHost=true -o "$OUT/publish"

echo "Creating app bundle..."
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$OUT/publish/." "$APP/Contents/MacOS/"

# macOS wants .icns with every size; build it from the 1024px logo (scripts/make-logo.py).
ICONSET="$OUT/Keystone.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
    sips -z $size $size "$ROOT/src/Keystone/Assets/keystone.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    sips -z $((size * 2)) $((size * 2)) "$ROOT/src/Keystone/Assets/keystone.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Keystone.icns"
rm -rf "$ICONSET"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Keystone</string>
    <key>CFBundleDisplayName</key><string>Keystone</string>
    <key>CFBundleIdentifier</key><string>ch.keystone.app</string>
    <key>CFBundleExecutable</key><string>Keystone</string>
    <key>CFBundleIconFile</key><string>Keystone.icns</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSApplicationCategoryType</key><string>public.app-category.finance</string>
</dict>
</plist>
PLIST

# Ad-hoc signature: required to run on Apple Silicon. Not notarized, so only for this Mac.
codesign --force --deep --sign - "$APP"

echo "Built $APP"

if [[ "${1:-}" == "--install" ]]; then
    rm -rf /Applications/Keystone.app
    cp -R "$APP" /Applications/
    echo "Installed /Applications/Keystone.app"
    # The app used to be called Runway; remove that older copy so only one is installed.
    if [[ -d /Applications/Runway.app ]]; then
        rm -rf /Applications/Runway.app
        echo "Removed the old /Applications/Runway.app (your data is kept)"
    fi
fi
