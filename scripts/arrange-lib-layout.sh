#!/usr/bin/env bash
# 把发布目录整理成：外层保留可执行文件、宿主原生库和说明；托管依赖进 lib/
# 注意：hostfxr/coreclr 等必须留在 exe 旁，否则 Windows 会误报“需要安装 .NET”。
set -euo pipefail

ROOT="${1:?用法: arrange-lib-layout.sh <publish-dir> <main-exe-stem>}"
STEM="${2:?需要主程序名，不含扩展名，例如 KeepPassword}"

if [[ ! -d "$ROOT" ]]; then
  echo "目录不存在: $ROOT" >&2
  exit 1
fi

LIB="$ROOT/lib"
mkdir -p "$LIB"

is_runtime_native() {
  local name="$1"
  case "$name" in
    hostfxr.dll|hostpolicy.dll|coreclr.dll|clrjit.dll|clrgc.dll|clretwrc.dll| \
    mscordaccore.dll|mscordbi.dll|mscorrc.dll| \
    createdump|createdump.exe| \
    libcoreclr.so|libclrjit.so|libhostfxr.so|libhostpolicy.so| \
    libcoreclr.dylib|libclrjit.dylib|libhostfxr.dylib|libhostpolicy.dylib)
      return 0
      ;;
    mscordaccore_*.dll|Microsoft.DiaSymReader.Native.*.dll)
      return 0
      ;;
    # Avalonia / Skia 原生库：在托管代码启动前也可能被加载
    av_libglesv2.dll|libSkiaSharp.dll|libHarfBuzzSharp.dll| \
    libSkiaSharp.so|libHarfBuzzSharp.so|libAvaloniaNative.so| \
    libSkiaSharp.dylib|libHarfBuzzSharp.dylib|libAvaloniaNative.dylib)
      return 0
      ;;
  esac
  return 1
}

shopt -s nullglob
for path in "$ROOT"/*; do
  name="$(basename "$path")"
  case "$name" in
    "$STEM"|"$STEM.exe"|"$STEM.dll")
      continue
      ;;
    Uninstall|Uninstall.exe|使用说明.txt|README.txt|README.md|app.ico|logo.png)
      continue
      ;;
    lib|native-host|extension)
      continue
      ;;
    *.pdb)
      rm -f "$path"
      continue
      ;;
  esac

  if [[ -d "$path" ]]; then
    if [[ "$name" == "runtimes" || "$name" == "cs" || "$name" == "de" || "$name" == "es" || "$name" == "fr" || "$name" == "it" || "$name" == "ja" || "$name" == "ko" || "$name" == "pl" || "$name" == "pt-BR" || "$name" == "ru" || "$name" == "tr" || "$name" == "zh-Hans" || "$name" == "zh-Hant" ]]; then
      rm -rf "$LIB/$name"
      mv "$path" "$LIB/"
    fi
    continue
  fi

  if is_runtime_native "$name"; then
    continue
  fi

  case "$name" in
    *.dll|*.so|*.dylib)
      mv -f "$path" "$LIB/"
      ;;
    *.json)
      if [[ "$name" == "$STEM.deps.json" || "$name" == "$STEM.runtimeconfig.json" ]]; then
        continue
      fi
      mv -f "$path" "$LIB/"
      ;;
  esac
done

# 让宿主也能在 lib/ 里找到托管程序集
CONFIG="$ROOT/$STEM.runtimeconfig.json"
if [[ -f "$CONFIG" ]]; then
  python3 - "$CONFIG" <<'PY'
import json, sys
path = sys.argv[1]
data = json.load(open(path, encoding="utf-8"))
opts = data.setdefault("runtimeOptions", {})
paths = opts.setdefault("additionalProbingPaths", [])
if "lib" not in paths:
    paths.insert(0, "lib")
json.dump(data, open(path, "w", encoding="utf-8"), indent=2)
print("patched", path)
PY
fi

echo "已整理: $ROOT"
find "$ROOT" -maxdepth 1 -type f | sort
echo "--- lib 文件数: $(find "$LIB" -maxdepth 1 -type f | wc -l) ---"
