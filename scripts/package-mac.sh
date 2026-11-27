#!/usr/bin/env bash
# Builds Keystone.app; with --install copies it to /Applications, with --dmg also makes a disk image to share.
# Usage: scripts/package-mac.sh [--install] [--dmg] [--arch arm64|x64]
#   --arch   the Mac to build for: arm64 (Apple silicon) or x64 (Intel); default: this Mac
set -euo pipefail

INSTALL=false
DMG=false
ARCH=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --install) INSTALL=true ;;
        --dmg) DMG=true ;;
        --arch) ARCH="${2:?--arch needs arm64 or x64}"; shift ;;
        *) echo "Unknown option: $1" >&2; exit 1 ;;
    esac
    shift
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/src/Keystone/Keystone.csproj"
OUT="$ROOT/artifacts"
APP="$OUT/Keystone.app"
VERSION="${VERSION:-$(sed -n "s:.*<Version>\(.*\)</Version>.*:\1:p" "$PROJECT")}"

if [[ -z "$ARCH" ]]; then
    [[ "$(uname -m)" == arm64 ]] && ARCH=arm64 || ARCH=x64
fi
case "$ARCH" in
    arm64) RID=osx-arm64; SWIFT_ARCH=arm64 ;;
    x64) RID=osx-x64; SWIFT_ARCH=x86_64 ;;
    *) echo "--arch must be arm64 or x64" >&2; exit 1 ;;
esac

echo "Publishing for $RID..."
rm -rf "$OUT/publish" "$APP" "$OUT/Runway.app"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
    -p:UseAppHost=true -p:Version="$VERSION" -o "$OUT/publish"

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
    <key>LSMinimumSystemVersion</key><string>14.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSApplicationCategoryType</key><string>public.app-category.finance</string>
    <key>NSCalendarsFullAccessUsageDescription</key><string>Keystone keeps your appointments in step with the calendar you choose.</string>
    <key>NSCalendarsUsageDescription</key><string>Keystone keeps your appointments in step with the calendar you choose.</string>
</dict>
</plist>
PLIST

# The desktop widget (open tasks, bills, appointments): a small WidgetKit extension in Swift, built when the
# Xcode command line tools are there. It reads the summary Keystone writes next to its database.
WIDGET="$APP/Contents/PlugIns/KeystoneWidget.appex"
if xcrun --find swiftc >/dev/null 2>&1; then
    echo "Building the widget..."
    mkdir -p "$WIDGET/Contents/MacOS"
    xcrun swiftc -target "$SWIFT_ARCH-apple-macos14.0" -sdk "$(xcrun --sdk macosx --show-sdk-path)" \
        -parse-as-library -application-extension -O -framework WidgetKit -framework SwiftUI \
        -Xlinker -e -Xlinker _NSExtensionMain \
        "$ROOT/src/KeystoneWidget/KeystoneWidget.swift" -o "$WIDGET/Contents/MacOS/KeystoneWidget"
    cat > "$WIDGET/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Keystone</string>
    <key>CFBundleDisplayName</key><string>Keystone</string>
    <key>CFBundleIdentifier</key><string>ch.keystone.app.widget</string>
    <key>CFBundleExecutable</key><string>KeystoneWidget</string>
    <key>CFBundlePackageType</key><string>XPC!</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>LSMinimumSystemVersion</key><string>14.0</string>
    <key>NSExtension</key>
    <dict>
        <key>NSExtensionPointIdentifier</key><string>com.apple.widgetkit-extension</string>
    </dict>
</dict>
</plist>
PLIST
else
    echo "Xcode command line tools not found: building without the desktop widget."
fi

# Ad-hoc signature: required to run on Apple silicon. Not notarized: macOS asks once before opening a downloaded copy.
codesign --force --deep --sign - "$APP"
if [[ -d "$WIDGET" ]]; then
    # The widget needs its own sandbox entitlements; then the app is sealed again around it.
    codesign --force --sign - --entitlements "$ROOT/src/KeystoneWidget/KeystoneWidget.entitlements" "$WIDGET"
    codesign --force --sign - "$APP"
fi

echo "Built $APP"

if $DMG; then
    # A disk image with the app and a shortcut to Applications, to drag it across.
    DMG_PATH="$OUT/Keystone-$VERSION-$ARCH.dmg"
    STAGE="$OUT/dmg"
    rm -rf "$STAGE" "$DMG_PATH"
    mkdir -p "$STAGE"
    cp -R "$APP" "$STAGE/"
    ln -s /Applications "$STAGE/Applications"
    hdiutil create -volname "Keystone $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG_PATH" >/dev/null
    rm -rf "$STAGE"
    echo "Built $DMG_PATH"
fi

if $INSTALL; then
    rm -rf /Applications/Keystone.app
    cp -R "$APP" /Applications/
    echo "Installed /Applications/Keystone.app"
    # Tell macOS about the app (and its widget) now, rather than at its next scan.
    /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister \
        -f /Applications/Keystone.app >/dev/null 2>&1 || true
    # The app used to be called Runway; remove that older copy so only one is installed.
    if [[ -d /Applications/Runway.app ]]; then
        rm -rf /Applications/Runway.app
        echo "Removed the old /Applications/Runway.app (your data is kept)"
    fi
fi
