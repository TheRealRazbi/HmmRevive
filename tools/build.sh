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
# Launcher (build/launcher/HMM-Revive.exe finds this repo's ./instance by itself). A running one locks its exe: skip then.
if ! out=$(dotnet build tools/Launcher -c Release -o build/launcher -v q -nologo 2>&1); then echo "$out" | grep -E " error " | sort -u; echo "LAUNCHER BUILD FAILED (is HMM-Revive.exe running?)"; fi
rm -f "$INST/HMM_Data/Managed/0Harmony.dll"
./build/patcher/Patcher.exe "$GAME" "$INST" build/mod

# Older game build (optional): HMM_2017_DIR = the folder of a supported 2017 copy of the game (set it in the environment
# or in tools/local.env, which git ignores). Builds its mod into build/mod/2017 and its copy into "$INST-2017".
[ -f tools/local.env ] && . tools/local.env
if [ -n "${HMM_2017_DIR:-}" ]; then
    if ! out=$(dotnet build mod/HmmRevive.Legacy -c Release -v q -nologo -p:GameManaged="$HMM_2017_DIR/HMM_Data/Managed" 2>&1); then echo "$out" | grep -E " error " | sort -u; echo "LEGACY MOD BUILD FAILED"; exit 1; fi
    mkdir -p build/mod/2017 && cp mod/HmmRevive.Legacy/bin/Release/net35/{HmmReviveLegacy.dll,balance-2017.txt} build/mod/2017/
    powershell -NoProfile -Command "\$exe = [IO.Path]::GetFullPath('$INST-2017\HMM.exe'); Get-Process HMM -EA SilentlyContinue | ? { \$_.Path -eq \$exe } | Stop-Process -Force; Start-Sleep -Milliseconds 700" || true
    ./build/patcher/Patcher.exe "$HMM_2017_DIR" "$INST-2017" build/mod
else
    echo "(HMM_2017_DIR not set: skipped the 2017 build's mod and copy)"
fi
