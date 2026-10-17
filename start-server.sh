#!/usr/bin/env bash
set -euo pipefail

root="$(cd -- "$(dirname -- "$0")" && pwd)"
case "$(uname -s)" in
  Darwin) os=osx ;;
  Linux) os=linux ;;
  *) echo 'Unsupported platform.' >&2; exit 1 ;;
esac
case "$(uname -m)" in
  x86_64) arch=x64 ;;
  arm64|aarch64) arch=arm64 ;;
  *) echo 'Unsupported architecture.' >&2; exit 1 ;;
esac

# 无参数时默认开启本机 GM 和内嵌 GM 网页（只监听 127.0.0.1）。
if [[ $# -eq 0 ]]; then
  echo 'GM web (embedded, local only): http://127.0.0.1:23103/'
  set -- --gm-port 23102 --gm-web-port 23103
fi

for binary in "$root/DfoServer" "$root/dist/$os-$arch/DfoServer"; do
  if [[ -x "$binary" ]]; then
    cd -- "$(dirname -- "$binary")"
    exec "$binary" "$@"
  fi
done

dll="$root/Server/DfoServer/bin/Debug/net10.0/DfoServer.dll"
if [[ -f "$dll" ]] && command -v dotnet >/dev/null 2>&1; then
  cd -- "$(dirname -- "$dll")"
  exec dotnet "$dll" "$@"
fi
echo 'DfoServer was not found. Build first: dotnet build ServerS5A9.sln -c Debug' >&2
exit 1
