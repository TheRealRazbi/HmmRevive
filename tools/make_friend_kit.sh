#!/usr/bin/env bash
# Package build/HMM-Revive-friend-kit-<version>.zip: self-contained patcher + mod + setup/play scripts.
# Contains only this project's code (no Hoplon files); friends patch their own Steam install.
# The version comes from ./VERSION (also baked into HmmRevive.dll, which the kit scripts read).
#   tools/make_friend_kit.sh preview    for a GitHub pre-release: HMM-Revive-friend-kit-<version>-preview.zip
set -euo pipefail
cd "$(dirname "$0")/.."
V=$(tr -d '[:space:]' < VERSION)
case "${1:-}" in "") LABEL=$V; SUFFIX= ;; preview) LABEL="$V (preview)"; SUFFIX=-preview ;; *) echo "usage: tools/make_friend_kit.sh [preview]"; exit 1 ;; esac
KIT=build/friend-kit
ZIP="build/HMM-Revive-friend-kit-$V$SUFFIX.zip"
rm -rf "$KIT" "$ZIP" && mkdir -p "$KIT/mod"
if ! out=$(dotnet build mod/HmmRevive -c Release -v q -nologo 2>&1); then echo "$out" | grep -E " error " | sort -u; echo "MOD BUILD FAILED"; exit 1; fi
cp mod/HmmRevive/bin/Release/net35/HmmRevive.dll "$KIT/mod/"
if ! out=$(dotnet publish tools/Patcher -c Release -r win-x64 --self-contained -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true -o build/patcher-kit -v q -nologo 2>&1); then echo "$out" | grep -E " error " | sort -u; echo "PATCHER PUBLISH FAILED"; exit 1; fi
cp build/patcher-kit/Patcher.exe "$KIT/"
if ! out=$(dotnet build tools/Launcher -c Release -o build/launcher-kit -v q -nologo 2>&1); then echo "$out" | grep -E " error " | sort -u; echo "LAUNCHER BUILD FAILED"; exit 1; fi
cp build/launcher-kit/HMM-Revive.exe build/launcher-kit/HMM-Revive.exe.config "$KIT/"
cp tools/friend-kit/* launcher/skins.txt "$KIT/"
sed -i "s/@VERSION@/$LABEL/" "$KIT/README.txt" "$KIT/LEIA-ME.txt"
cp CHANGELOG.md "$KIT/CHANGELOG.txt"
# Leak check before zipping, when tools/check_public.py is present (the maintainer's copy). Patcher.dll is the
# uncompressed assembly bundled into Patcher.exe.
if [ -f tools/check_public.py ]; then
    python tools/check_public.py "$KIT" tools/Patcher/obj/Release/net8.0/win-x64/Patcher.dll || { echo "KIT LEAK CHECK FAILED"; exit 1; }
fi
powershell -NoProfile -Command "Compress-Archive -Path '$KIT/*' -DestinationPath '$ZIP'"
ls -la "$KIT" "$ZIP"
