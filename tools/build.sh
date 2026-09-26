#!/usr/bin/env bash
# Build mod + patcher and (re)create the patched, hardlinked game instance in ./instance
#   HMM_INSTANCE=instance-dev tools/build.sh   builds a second instance (hardlinked too, no extra disk) and only stops
#                                               the HMM processes running from that one, so a match in ./instance survives.
set -euo pipefail
cd "$(dirname "$0")/.."
INST="${HMM_INSTANCE:-instance}"
powershell -NoProfile -Command "\$exe = [IO.Path]::GetFullPath('$INST\HMM.exe'); Get-Process HMM -EA SilentlyContinue | ? { \$_.Path -eq \$exe } | Stop-Process -Force; Start-Sleep -Milliseconds 700" || true
GAME="${HMM_GAME_DIR:-C:/Program Files (x86)/Steam/steamapps/common/Heavy Metal Machines}"
if ! out=$(dotnet build mod/HmmRevive -c Release -v q -nologo 2>&1); then echo "$out" | grep -E " error " | sort -u; echo "MOD BUILD FAILED"; exit 1; fi
rm -rf build/mod && mkdir -p build/mod && cp mod/HmmRevive/bin/Release/net35/HmmRevive.dll build/mod/
if ! out=$(dotnet build tools/Patcher -c Release -o build/patcher -v q -nologo 2>&1); then echo "$out" | grep -E " error " | sort -u; echo "PATCHER BUILD FAILED"; exit 1; fi
rm -f "$INST/HMM_Data/Managed/0Harmony.dll"
./build/patcher/Patcher.exe "$GAME" "$INST" build/mod
