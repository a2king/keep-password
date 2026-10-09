#!/usr/bin/env bash
# 打 Windows x64 安装包：主程序 lib 布局 + 卸载程序 + Setup.exe
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export PATH="${HOME}/.dotnet:${PATH}"
export DOTNET_ROOT="${HOME}/.dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

OUT="${1:-/tmp/kp-windows-package}"
STAGING="$OUT/staging"
PAYLOAD_DIR="$OUT/payload"
SETUP_OUT="$OUT/setup"
RID=win-x64

rm -rf "$OUT"
mkdir -p "$STAGING" "$PAYLOAD_DIR" "$SETUP_OUT"

echo "== 发布主程序 =="
dotnet publish "$ROOT/src/KeepPassword.App/KeepPassword.App.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:DebugType=none -p:DebugSymbols=false \
  -o "$STAGING"

echo "== 发布 Native Host =="
mkdir -p "$STAGING/native-host"
dotnet publish "$ROOT/src/KeepPassword.NativeHost/KeepPassword.NativeHost.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:DebugType=none -p:DebugSymbols=false \
  -o "$STAGING/native-host"

echo "== 整理 lib 布局 =="
bash "$ROOT/scripts/arrange-lib-layout.sh" "$STAGING" KeepPassword
bash "$ROOT/scripts/arrange-lib-layout.sh" "$STAGING/native-host" KeepPassword.NativeHost

echo "== 扩展与说明 =="
cp -a "$ROOT/extension" "$STAGING/extension"
python3 - <<'PY' > "$STAGING/使用说明.txt"
text = """Keep Password

双击 KeepPassword.exe 启动。这是自包含发布，不需要另装 .NET。

依赖 DLL 在 lib 文件夹。请保持目录结构完整，不要只拷贝 exe。

卸载请运行 Uninstall.exe。默认不会删除缓存目录；需要清除保险库时，在卸载界面勾选「同时删除缓存目录」。

浏览器扩展见 extension 文件夹，按里面的 register-windows.ps1 注册 Native Messaging。
"""
import sys
sys.stdout.buffer.write(b"\xef\xbb\xbf" + text.encode("utf-8"))
PY

echo "== 发布卸载程序（单文件） =="
dotnet publish "$ROOT/src/KeepPassword.Uninstall/KeepPassword.Uninstall.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none -p:DebugSymbols=false \
  -o "$OUT/uninstall-build"
cp -f "$OUT/uninstall-build/Uninstall.exe" "$STAGING/Uninstall.exe"

echo "== 打包 payload =="
(
  cd "$STAGING"
  zip -r -q -9 "$OUT/payload.zip" .
)

echo "== 发布安装程序 =="
cp -f "$OUT/payload.zip" "$ROOT/src/KeepPassword.Setup/payload.zip"
dotnet publish "$ROOT/src/KeepPassword.Setup/KeepPassword.Setup.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none -p:DebugSymbols=false \
  -o "$SETUP_OUT"
rm -f "$ROOT/src/KeepPassword.Setup/payload.zip"

cp -f "$SETUP_OUT/KeepPassword.Setup.exe" "$OUT/KeepPassword-Setup-win-x64.exe"
cp -f "$OUT/payload.zip" "$OUT/KeepPassword-win-x64-portable.zip"
# 便携版也提供一份整理后的目录 zip
(
  cd "$STAGING"
  zip -r -q -9 "$OUT/KeepPassword-win-x64.zip" .
)

echo "== 完成 =="
ls -lh "$OUT/KeepPassword-Setup-win-x64.exe" "$OUT/KeepPassword-win-x64.zip"
echo "外层文件示例："
find "$STAGING" -maxdepth 1 -type f | sort
echo "lib 数量: $(find "$STAGING/lib" -type f | wc -l)"
