#!/usr/bin/env bash
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
data_root="$root"
if [[ "$(basename -- "$root")" == '.95jp-runtime' ]]; then data_root="$(dirname -- "$root")"; fi
cd -- "$root"
exec dotnet "$root/DfoServer.dll" --gm-port 23102 --gm-web-port 23103 --data-dir "$data_root/Data" --log-dir "$data_root/Logs" --resources-dir "$data_root/Resources" --pvf-path "$data_root/Data/Pvf/Script.pvf" "$@"
