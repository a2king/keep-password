#!/usr/bin/env bash
# 打 Linux x64 压缩包：自包含运行时 + 本机连接程序 + 浏览器扩展注册脚本 + .desktop 文件。
# 用法: scripts/package-linux.sh [输出目录]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:-$ROOT/artifacts/linux}"
RID=linux-x64
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)"
[[ -n "$VERSION" ]] || { echo "无法从 Directory.Build.props 读取版本号" >&2; exit 1; }
export DOTNET_CLI_TELEMETRY_OPTOUT=1

NAME="KeepPassword-${VERSION}-${RID}"
DIR="$OUT/$NAME"
rm -rf "$OUT"
mkdir -p "$DIR"
dotnet publish "$ROOT/src/KeepPassword.App/KeepPassword.App.csproj" -c Release -r "$RID" --self-contained true \
  -p:DebugType=none -p:DebugSymbols=false -o "$DIR" --nologo -v q
dotnet publish "$ROOT/src/KeepPassword.NativeHost/KeepPassword.NativeHost.csproj" -c Release -r "$RID" --self-contained true \
  -p:DebugType=none -p:DebugSymbols=false -o "$OUT/native-host" --nologo -v q
cp -n "$OUT/native-host/"* "$DIR/" 2>/dev/null || true
rm -rf "$OUT/native-host"
chmod +x "$DIR/KeepPassword" "$DIR/KeepPassword.NativeHost"
mkdir -p "$DIR/extension"
cp -R "$ROOT/extension/." "$DIR/extension/"
rm -rf "$DIR/extension/node_modules" "$DIR/extension/tests"
cp "$ROOT/src/KeepPassword.App/Assets/logo-256.png" "$DIR/keep-password.png"

cat > "$DIR/install.sh" <<'SH'
#!/usr/bin/env bash
# 把 Keep Password 加进应用菜单（只写当前用户目录，不需要 sudo）。
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mkdir -p "$APPS"
cat > "$APPS/keep-password.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Keep Password
Comment=本地加密的密码管理器
Exec="$HERE/KeepPassword"
Icon=$HERE/keep-password.png
Terminal=false
Categories=Utility;Security;
StartupWMClass=KeepPassword
DESKTOP
echo "已添加到应用菜单：$APPS/keep-password.desktop"
echo "浏览器扩展连接：\"$HERE/extension/register-unix.sh\" \"$HERE/KeepPassword.NativeHost\" <扩展 ID>"
SH
chmod +x "$DIR/install.sh"

cat > "$DIR/README.txt" <<TXT
Keep Password ${VERSION}（Linux x64，自包含，不需要另装 .NET）

运行：./KeepPassword
加入应用菜单：./install.sh
数据目录：~/.local/share/KeepPassword；设置：~/.config/KeepPassword
说明：Linux 上关闭窗口会最小化到任务栏（不少桌面没有托盘区）；程序快捷键自动填充尚未实现，可用浏览器扩展。
TXT

(cd "$OUT" && tar czf "$NAME.tar.gz" "$NAME")
sha256sum "$OUT/$NAME.tar.gz"
echo "完成: $OUT/$NAME.tar.gz"
