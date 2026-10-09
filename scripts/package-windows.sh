#!/usr/bin/env bash
# 打 Windows x64 安装包：一份自包含运行时 + 压缩 payload + 向导安装器
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export PATH="${HOME}/.dotnet:${PATH}"
export DOTNET_ROOT="${HOME}/.dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

OUT="${1:-/tmp/kp-windows-package}"
STAGING="$OUT/staging"
SETUP_OUT="$OUT/setup"
NH_OUT="$OUT/native-host-build"
UNINSTALL_OUT="$OUT/uninstall-build"
RID=win-x64

rm -rf "$OUT"
mkdir -p "$STAGING/extension" "$SETUP_OUT" "$NH_OUT" "$UNINSTALL_OUT"

echo "== 发布主程序（自包含，共用运行时，非单文件） =="
dotnet publish "$ROOT/src/KeepPassword.App/KeepPassword.App.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false \
  -p:DebugType=none -p:DebugSymbols=false \
  -p:PublishReadyToRun=true \
  -o "$STAGING"

echo "== 发布 Native Host（自包含到临时目录，只并入宿主文件） =="
dotnet publish "$ROOT/src/KeepPassword.NativeHost/KeepPassword.NativeHost.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false \
  -p:DebugType=none -p:DebugSymbols=false \
  -o "$NH_OUT"

# 与主程序共用同一目录里的运行时，避免再打一份完整 runtime
cp -f "$NH_OUT/KeepPassword.NativeHost.exe" "$STAGING/"
cp -f "$NH_OUT/KeepPassword.NativeHost.dll" "$STAGING/"
cp -f "$NH_OUT/KeepPassword.NativeHost.deps.json" "$STAGING/"
cp -f "$NH_OUT/KeepPassword.NativeHost.runtimeconfig.json" "$STAGING/"

echo "== 发布卸载程序（单文件压缩，便于自我删除） =="
dotnet publish "$ROOT/src/KeepPassword.Uninstall/KeepPassword.Uninstall.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -p:DebugSymbols=false \
  -o "$UNINSTALL_OUT"
cp -f "$UNINSTALL_OUT/Uninstall.exe" "$STAGING/Uninstall.exe"

# 清理调试残留
find "$STAGING" -name '*.pdb' -delete
rm -f "$STAGING/createdump.exe" || true

echo "== 扩展与说明 =="
cp -a "$ROOT/extension/." "$STAGING/extension/"
python3 - <<'PY' > "$STAGING/使用说明.txt"
text = """Keep Password

这是自包含安装包，不需要另外安装 .NET。

双击 KeepPassword.exe 启动。
卸载请运行 Uninstall.exe。默认不会删除缓存目录。

浏览器扩展见 extension 文件夹。
Native Messaging 宿主为本目录下的 KeepPassword.NativeHost.exe，例如：

  .\\extension\\register-windows.ps1 -HostExe .\\KeepPassword.NativeHost.exe -ExtensionId <扩展ID>
"""
import sys
sys.stdout.buffer.write(b"\xef\xbb\xbf" + text.encode("utf-8"))
PY

echo "== 高压缩打包 payload =="
(
  cd "$STAGING"
  # -9 最大压缩；排除无用元数据
  zip -r -q -9 "$OUT/payload.zip" . -x '*.pdb'
)
echo "payload: $(du -h "$OUT/payload.zip" | awk '{print $1}') / staging: $(du -sh "$STAGING" | awk '{print $1}')"

echo "== 发布安装向导（单文件 + 内嵌压缩包） =="
cp -f "$OUT/payload.zip" "$ROOT/src/KeepPassword.Setup/payload.zip"
dotnet publish "$ROOT/src/KeepPassword.Setup/KeepPassword.Setup.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -p:DebugSymbols=false \
  -o "$SETUP_OUT"
rm -f "$ROOT/src/KeepPassword.Setup/payload.zip"

cp -f "$SETUP_OUT/KeepPassword.Setup.exe" "$OUT/KeepPassword-Setup-win-x64.exe"
(
  cd "$STAGING"
  zip -r -q -9 "$OUT/KeepPassword-win-x64.zip" .
)

echo "== 完成 =="
ls -lh "$OUT/KeepPassword-Setup-win-x64.exe" "$OUT/KeepPassword-win-x64.zip" "$OUT/payload.zip"
echo "安装目录文件数: $(find "$STAGING" -type f | wc -l)"
echo "外层可执行文件:"
ls -lh "$STAGING/KeepPassword.exe" "$STAGING/KeepPassword.NativeHost.exe" "$STAGING/Uninstall.exe"
