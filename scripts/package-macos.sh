#!/usr/bin/env bash
# 打 macOS 安装包：Keep Password.app（自包含运行时 + 本机连接程序），临时签名后压缩成 zip。
#
# 必须在 Mac 上运行：Apple 芯片的 Mac 拒绝运行没有代码签名的 arm64 程序，在 Linux / Windows 上
# 交叉发布出来的主程序没有签名，拷到 Mac 上会被系统直接杀掉（Killed: 9）。
# 默认做 ad-hoc 签名（codesign -s -），可以在本机运行；对外分发需要 Developer ID 签名和公证，
# 把证书名称传给 MAC_SIGN_IDENTITY 即可（公证另做）。
#
# 用法: scripts/package-macos.sh [输出目录] [osx-arm64|osx-x64]
#       在非 Mac 上只检查 .app 结构：MAC_NO_SIGN=1 scripts/package-macos.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:-$ROOT/artifacts/macos}"
RID="${2:-osx-arm64}"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)"
[[ -n "$VERSION" ]] || { echo "无法从 Directory.Build.props 读取版本号" >&2; exit 1; }

if [[ "$(uname -s)" != "Darwin" && "${MAC_NO_SIGN:-}" != "1" ]]; then
  echo "请在 Mac 上运行（需要 codesign）。只想检查目录结构时设置 MAC_NO_SIGN=1。" >&2
  exit 1
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1
APP="$OUT/Keep Password.app"
MACOS="$APP/Contents/MacOS"
rm -rf "$OUT"
mkdir -p "$MACOS" "$APP/Contents/Resources"

dotnet publish "$ROOT/src/KeepPassword.App/KeepPassword.App.csproj" -c Release -r "$RID" --self-contained true \
  -p:DebugType=none -p:DebugSymbols=false -p:UseAppHost=true -o "$MACOS" --nologo -v q
dotnet publish "$ROOT/src/KeepPassword.NativeHost/KeepPassword.NativeHost.csproj" -c Release -r "$RID" --self-contained true \
  -p:DebugType=none -p:DebugSymbols=false -o "$OUT/native-host" --nologo -v q
cp -n "$OUT/native-host/"* "$MACOS/" 2>/dev/null || true
rm -rf "$OUT/native-host"
mkdir -p "$APP/Contents/Resources/extension"
cp -R "$ROOT/extension/." "$APP/Contents/Resources/extension/"
rm -rf "$APP/Contents/Resources/extension/node_modules" "$APP/Contents/Resources/extension/tests"

# 图标：Mac 上用 sips + iconutil 从 logo.png 生成 .icns；其他系统跳过。
ICON_KEY=""
if command -v iconutil >/dev/null 2>&1; then
  SET="$OUT/AppIcon.iconset"
  mkdir -p "$SET"
  for size in 16 32 128 256 512; do
    sips -z $size $size "$ROOT/src/KeepPassword.App/Assets/logo.png" --out "$SET/icon_${size}x${size}.png" >/dev/null
    sips -z $((size * 2)) $((size * 2)) "$ROOT/src/KeepPassword.App/Assets/logo.png" --out "$SET/icon_${size}x${size}@2x.png" >/dev/null
  done
  iconutil -c icns "$SET" -o "$APP/Contents/Resources/AppIcon.icns"
  rm -rf "$SET"
  ICON_KEY="<key>CFBundleIconFile</key><string>AppIcon</string>"
fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Keep Password</string>
  <key>CFBundleDisplayName</key><string>Keep Password</string>
  <key>CFBundleIdentifier</key><string>com.keeppassword.app</string>
  <key>CFBundleExecutable</key><string>KeepPassword</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
  ${ICON_KEY}
</dict>
</plist>
PLIST

if [[ "${MAC_NO_SIGN:-}" == "1" ]]; then
  echo "已生成（未签名，只用于检查结构）: $APP"
  exit 0
fi

IDENTITY="${MAC_SIGN_IDENTITY:--}"
# ad-hoc 签名只为让 Apple 芯片能运行，不开强化运行时；正式证书签名（准备公证）才开，并附上 .NET 需要的 JIT 授权。
SIGN_ARGS=(--force --timestamp=none -s "$IDENTITY")
if [[ "$IDENTITY" != "-" ]]; then
  SIGN_ARGS=(--force --timestamp --options runtime --entitlements "$ROOT/scripts/macos-entitlements.plist" -s "$IDENTITY")
fi
# 先签每个原生库和两个可执行文件，再签整个 .app。
find "$MACOS" -type f \( -name "*.dylib" -o -name "KeepPassword" -o -name "KeepPassword.NativeHost" -o -name "createdump" \) -print0 |
  while IFS= read -r -d '' file; do codesign "${SIGN_ARGS[@]}" "$file"; done
codesign "${SIGN_ARGS[@]}" "$APP"
codesign --verify --deep --strict "$APP"
echo "签名校验通过（$IDENTITY）"

ZIP="$OUT/KeepPassword-${VERSION}-${RID}.zip"
(cd "$OUT" && ditto -c -k --keepParent "Keep Password.app" "$(basename "$ZIP")")
shasum -a 256 "$ZIP"
echo "完成: $ZIP"
echo "浏览器扩展连接：运行 \"$APP/Contents/Resources/extension/register-unix.sh\" \"$MACOS/KeepPassword.NativeHost\" <扩展 ID>"
