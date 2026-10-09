#!/usr/bin/env bash
# 把发布目录整理成：外层只留可执行文件/说明/配置，依赖进 lib/
set -euo pipefail

ROOT="${1:?用法: arrange-lib-layout.sh <publish-dir> <main-exe-stem>}"
STEM="${2:?需要主程序名，不含扩展名，例如 KeepPassword}"

if [[ ! -d "$ROOT" ]]; then
  echo "目录不存在: $ROOT" >&2
  exit 1
fi

LIB="$ROOT/lib"
mkdir -p "$LIB"

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
    *.pdb|createdump|createdump.exe)
      if [[ "$name" == *.pdb ]]; then
        rm -f "$path"
      else
        mv -f "$path" "$LIB/"
      fi
      continue
      ;;
  esac

  if [[ -d "$path" ]]; then
    # runtimes 等目录整体挪进 lib
    if [[ "$name" == "runtimes" || "$name" == "cs" || "$name" == "de" || "$name" == "es" || "$name" == "fr" || "$name" == "it" || "$name" == "ja" || "$name" == "ko" || "$name" == "pl" || "$name" == "pt-BR" || "$name" == "ru" || "$name" == "tr" || "$name" == "zh-Hans" || "$name" == "zh-Hant" ]]; then
      rm -rf "$LIB/$name"
      mv "$path" "$LIB/"
    fi
    continue
  fi

  case "$name" in
    *.dll|*.so|*.dylib|*.json)
      # 主程序的 deps/runtimeconfig 必须留在 exe 旁
      if [[ "$name" == "$STEM.deps.json" || "$name" == "$STEM.runtimeconfig.json" ]]; then
        continue
      fi
      mv -f "$path" "$LIB/"
      ;;
  esac
done

echo "已整理: $ROOT"
find "$ROOT" -maxdepth 1 -type f | sort
echo "--- lib ---"
find "$LIB" -maxdepth 1 -type f | wc -l
