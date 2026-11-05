#!/usr/bin/env bash
# 打 Windows x64 安装包：一份自包含运行时 + 压缩 payload + 向导安装器
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export PATH="${HOME}/.dotnet:${PATH}"
export DOTNET_ROOT="${HOME}/.dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

OUT="${1:-/tmp/kp-windows-package}"
SETUP_SRC="$ROOT/src/KeepPassword.Setup"
# 打包中途失败时也要清掉写进源码目录的巨大临时文件（PowerShell 版用 finally，这里用 trap）。
trap 'rm -f "$SETUP_SRC/payload.zip" "$SETUP_SRC/payload.sha256"' EXIT
STAGING="$OUT/staging"
SETUP_OUT="$OUT/setup"
NH_OUT="$OUT/native-host-build"
UNINSTALL_OUT="$OUT/uninstall-build"
RID=win-x64
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)"
if [ -z "$VERSION" ]; then echo "无法从 Directory.Build.props 读取版本号" >&2; exit 1; fi
RELEASE_DLL="$OUT/release-tool/KeepPassword.Release.dll"

rm -rf "$OUT"
echo "== 发布者工具（更新签名） =="
dotnet build "$ROOT/tools/KeepPassword.Release/KeepPassword.Release.csproj" -c Release -o "$OUT/release-tool" --nologo -v q
set +e
dotnet "$RELEASE_DLL" status
TRUST=$?
set -e
# 退出码 0 = 已启用签名强制，3 = 未启用，其他 = 工具本身出错（必须中止，不能当成"未启用"）。
if [ "$TRUST" -ne 0 ] && [ "$TRUST" -ne 3 ]; then echo "无法确认更新签名状态（退出码 $TRUST）" >&2; exit 1; fi
if [ "$TRUST" -eq 0 ] && [ -z "${KP_UPDATE_KEY:-}" ]; then
  echo "仓库已启用更新签名强制，请设置 KP_UPDATE_KEY 指向私钥文件，否则发出去的更新会被客户端拒绝。" >&2
  exit 1
fi
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

echo "== 发布卸载程序（并入同一运行时，不再单独打单文件） =="
dotnet publish "$ROOT/src/KeepPassword.Uninstall/KeepPassword.Uninstall.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false \
  -p:DebugType=none -p:DebugSymbols=false \
  -o "$UNINSTALL_OUT"
cp -f "$UNINSTALL_OUT/Uninstall.exe" "$STAGING/"
cp -f "$UNINSTALL_OUT/Uninstall.dll" "$STAGING/"
cp -f "$UNINSTALL_OUT/Uninstall.deps.json" "$STAGING/"
cp -f "$UNINSTALL_OUT/Uninstall.runtimeconfig.json" "$STAGING/"

# 清理调试残留
find "$STAGING" -name '*.pdb' -delete
rm -f "$STAGING/createdump.exe" || true

echo "== 扩展与说明 =="
cp -a "$ROOT/extension/." "$STAGING/extension/"
rm -rf "$STAGING/extension/tests"
# 说明文字放在 scripts/install-readme.txt，与 PowerShell 版共用；带 UTF-8 BOM 方便 Windows 记事本识别。
python3 -c "import sys; sys.stdout.buffer.write(b'ï»¿' + open(sys.argv[1], 'rb').read())"   "$ROOT/scripts/install-readme.txt" > "$STAGING/使用说明.txt"

echo "== 高压缩打包 payload =="
(
  cd "$STAGING"
  # -9 最大压缩；排除无用元数据
  zip -r -q -9 "$OUT/payload.zip" . -x '*.pdb'
)
echo "payload: $(du -h "$OUT/payload.zip" | awk '{print $1}') / staging: $(du -sh "$STAGING" | awk '{print $1}')"

echo "== 计算 payload 完整性校验值 =="
python3 - <<PY
import hashlib, pathlib
payload = pathlib.Path(r"$OUT/payload.zip")
digest = hashlib.sha256(payload.read_bytes()).hexdigest()
pathlib.Path(r"$ROOT/src/KeepPassword.Setup/payload.sha256").write_text(digest + "\n", encoding="ascii")
print(digest)
PY

echo "== 发布安装向导（单文件 + 内嵌压缩包） =="
cp -f "$OUT/payload.zip" "$ROOT/src/KeepPassword.Setup/payload.zip"
dotnet publish "$ROOT/src/KeepPassword.Setup/KeepPassword.Setup.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -p:DebugSymbols=false \
  -o "$SETUP_OUT"
rm -f "$ROOT/src/KeepPassword.Setup/payload.zip" "$ROOT/src/KeepPassword.Setup/payload.sha256"

SETUP_EXE="$OUT/KeepPassword-Setup-win-x64.exe"
cp -f "$SETUP_OUT/KeepPassword.Setup.exe" "$SETUP_EXE"
# 注意：Linux 上没有 Authenticode 签名（需要 Windows 的 signtool 或 osslsigncode）。
# 要发出带 Authenticode 签名的正式包，请在 Windows 上用 artifacts/package.ps1。
python3 - <<PY
import hashlib, pathlib
setup = pathlib.Path(r"$SETUP_EXE")
digest = hashlib.sha256(setup.read_bytes()).hexdigest().upper()
pathlib.Path(r"$OUT/KeepPassword-Setup-win-x64.exe.sha256").write_text(digest + "\n", encoding="ascii")
print(setup.name, digest)
PY
(
  cd "$STAGING"
  zip -r -q -9 "$OUT/KeepPassword-win-x64.zip" .
)

# 更新签名：对"版本号 + 最终 Setup.exe 的 SHA-256"签名，客户端用内置公钥验签。
if [ -n "${KP_UPDATE_KEY:-}" ]; then
  echo "== 更新签名 =="
  dotnet "$RELEASE_DLL" sign --private "$KP_UPDATE_KEY" --file "$SETUP_EXE" --version "v$VERSION" --out "$SETUP_EXE.sig"
  dotnet "$RELEASE_DLL" verify --file "$SETUP_EXE" --version "v$VERSION" --sig "$SETUP_EXE.sig"
else
  echo "提示：未做更新签名（没有设置 KP_UPDATE_KEY），客户端目前只校验 SHA-256。" >&2
fi

echo "== 打包浏览器扩展 =="
EXT_VERSION="$(python3 -c "import json, sys; print(json.load(open(sys.argv[1], encoding='utf-8'))['version'])" "$ROOT/extension/manifest.json")"
EXT_ZIP="$OUT/KeepPassword-Chrome-Extension-v$EXT_VERSION.zip"
EDGE_ZIP="$OUT/KeepPassword-Edge-Extension-v$EXT_VERSION.zip"
rm -f "$EXT_ZIP" "$EDGE_ZIP"
(
  cd "$STAGING/extension"
  zip -r -q -9 "$EXT_ZIP" .
)
# Edge 商店拒绝 manifest 里的 key。本地加载仍用带 key 的 Chrome 包，以保持扩展 ID 不变。
EDGE_STAGE="$OUT/edge-extension"
rm -rf "$EDGE_STAGE"
cp -a "$STAGING/extension" "$EDGE_STAGE"
python3 - "$EDGE_STAGE/manifest.json" <<'PY'
import pathlib, re, sys
path = pathlib.Path(sys.argv[1])
text = re.sub(r'\r?\n[ \t]*"key"\s*:\s*"[^"]*"\s*,', "", path.read_text(encoding="utf-8"), count=1)
path.write_text(text, encoding="utf-8")
PY
(
  cd "$EDGE_STAGE"
  zip -r -q -9 "$EDGE_ZIP" .
)

echo "== 完成 =="
ls -lh "$OUT/KeepPassword-Setup-win-x64.exe" "$OUT/KeepPassword-Setup-win-x64.exe.sha256" "$OUT/KeepPassword-win-x64.zip" "$EXT_ZIP" "$EDGE_ZIP" "$OUT/payload.zip"
echo "安装目录文件数: $(find "$STAGING" -type f | wc -l)"
echo "外层可执行文件:"
ls -lh "$STAGING/KeepPassword.exe" "$STAGING/KeepPassword.NativeHost.exe" "$STAGING/Uninstall.exe"
