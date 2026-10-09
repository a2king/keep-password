#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "用法: $0 <NativeHost 可执行文件> <扩展 ID>" >&2
  exit 1
fi

host_exe=$(cd "$(dirname "$1")" && pwd)/$(basename "$1")
extension_id="$2"
manifest_name="com.keeppassword.host.json"

write_manifest() {
  local dir="$1"
  mkdir -p "$dir"
  cat > "$dir/$manifest_name" <<EOF
{
  "name": "com.keeppassword.host",
  "description": "Keep Password 本机消息宿主",
  "path": "$host_exe",
  "type": "stdio",
  "allowed_origins": [
    "chrome-extension://${extension_id}/"
  ]
}
EOF
  echo "已写入 $dir/$manifest_name"
}

case "$(uname -s)" in
  Darwin)
    write_manifest "$HOME/Library/Application Support/Google/Chrome/NativeMessagingHosts"
    write_manifest "$HOME/Library/Application Support/Microsoft Edge/NativeMessagingHosts"
    ;;
  Linux)
    write_manifest "$HOME/.config/google-chrome/NativeMessagingHosts"
    write_manifest "$HOME/.config/chromium/NativeMessagingHosts"
    write_manifest "$HOME/.config/microsoft-edge/NativeMessagingHosts"
    ;;
  *)
    echo "这个脚本只处理 macOS 和 Linux。Windows 请用 register-windows.ps1。" >&2
    exit 1
    ;;
esac

chmod +x "$host_exe"
