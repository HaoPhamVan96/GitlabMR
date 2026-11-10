#!/usr/bin/env bash
# Build GitLabMR cho macOS (mặc định osx-arm64) – đóng gói GitLabMR.app, tự chứa .NET runtime.
#   ./build.sh               -> publish/osx-arm64/GitLabMR.app + publish/GitLabMR-osx-arm64.zip
#   ./build.sh osx-x64       -> bản cho Mac Intel
#   ./build.sh linux-x64     -> bản Linux (thư mục + zip)
set -euo pipefail

RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")" && pwd)"
OUT="$ROOT/publish/$RID"
ZIP="$ROOT/publish/GitLabMR-$RID.zip"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/GitLabMR/GitLabMR.csproj")"

rm -rf "$OUT" "$ZIP"
dotnet publish "$ROOT/GitLabMR/GitLabMR.csproj" -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$OUT/bin"

if [[ "$RID" == osx-* ]]; then
  APP="$OUT/GitLabMR.app"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp -R "$OUT/bin/." "$APP/Contents/MacOS/"
  chmod +x "$APP/Contents/MacOS/GitLabMR"
  cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>GitLab MR</string>
  <key>CFBundleDisplayName</key><string>GitLab MR</string>
  <key>CFBundleIdentifier</key><string>com.haopv.gitlabmr</string>
  <key>CFBundleExecutable</key><string>GitLabMR</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
EOF
  rm -rf "$OUT/bin"
  # Ký ad-hoc (bắt buộc với Apple Silicon); không có chứng chỉ Apple nên chưa notarize
  if command -v codesign >/dev/null 2>&1; then codesign --force --deep --sign - "$APP"; fi
  if command -v ditto >/dev/null 2>&1; then
    (cd "$OUT" && ditto -c -k --keepParent GitLabMR.app "$ZIP")
  else
    (cd "$OUT" && zip -qry "$ZIP" GitLabMR.app)
  fi
else
  (cd "$OUT/bin" && zip -qry "$ZIP" .)
fi

echo "OK -> $OUT"
echo "Zip -> $ZIP"
