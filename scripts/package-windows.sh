#!/usr/bin/env bash
# 打 Windows x64 安装包：单文件自包含，不需要安装 .NET
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export PATH="${HOME}/.dotnet:${PATH}"
export DOTNET_ROOT="${HOME}/.dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

OUT="${1:-/tmp/kp-windows-package}"
STAGING="$OUT/staging"
SETUP_OUT="$OUT/setup"
RID=win-x64

rm -rf "$OUT"
mkdir -p "$STAGING/native-host" "$STAGING/extension" "$SETUP_OUT"

publish_single() {
  local project="$1"
  local dest="$2"
  dotnet publish "$project" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none -p:DebugSymbols=false \
    -o "$dest"
}

echo "== 发布主程序（单文件自包含，免安装 .NET） =="
publish_single "$ROOT/src/KeepPassword.App/KeepPassword.App.csproj" "$STAGING"

echo "== 发布 Native Host（单文件） =="
publish_single "$ROOT/src/KeepPassword.NativeHost/KeepPassword.NativeHost.csproj" "$STAGING/native-host"

# 单文件发布有时会留下多余 pdb/json，清掉外层杂项，只留 exe
find "$STAGING" -maxdepth 1 -type f ! -name 'KeepPassword.exe' -delete
find "$STAGING/native-host" -maxdepth 1 -type f ! -name 'KeepPassword.NativeHost.exe' -delete

echo "== 扩展与说明 =="
cp -a "$ROOT/extension/." "$STAGING/extension/"
python3 - <<'PY' > "$STAGING/使用说明.txt"
text = """Keep Password

这是自包含安装包，不需要另外安装 .NET，也不需要其它运行库。

双击 KeepPassword.exe 启动。

卸载请运行 Uninstall.exe。默认不会删除缓存目录；需要清除保险库时，在卸载界面勾选「同时删除缓存目录」。

浏览器扩展见 extension 文件夹，可按 register-windows.ps1 注册 Native Messaging。
宿主程序在 native-host\\KeepPassword.NativeHost.exe。
"""
import sys
sys.stdout.buffer.write(b"\xef\xbb\xbf" + text.encode("utf-8"))
PY

echo "== 发布卸载程序（单文件） =="
publish_single "$ROOT/src/KeepPassword.Uninstall/KeepPassword.Uninstall.csproj" "$OUT/uninstall-build"
cp -f "$OUT/uninstall-build/Uninstall.exe" "$STAGING/Uninstall.exe"

echo "== 打包 payload =="
(
  cd "$STAGING"
  zip -r -q -9 "$OUT/payload.zip" .
)

echo "== 发布安装程序 =="
cp -f "$OUT/payload.zip" "$ROOT/src/KeepPassword.Setup/payload.zip"
publish_single "$ROOT/src/KeepPassword.Setup/KeepPassword.Setup.csproj" "$SETUP_OUT"
rm -f "$ROOT/src/KeepPassword.Setup/payload.zip"

cp -f "$SETUP_OUT/KeepPassword.Setup.exe" "$OUT/KeepPassword-Setup-win-x64.exe"
(
  cd "$STAGING"
  zip -r -q -9 "$OUT/KeepPassword-win-x64.zip" .
)

echo "== 完成 =="
ls -lh "$OUT/KeepPassword-Setup-win-x64.exe" "$OUT/KeepPassword-win-x64.zip"
echo "安装目录外层："
find "$STAGING" -maxdepth 2 -type f | sort
