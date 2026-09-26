#!/usr/bin/env bash
# Summarize the latest instance run: mod log, game log (no shader spam), Unity exceptions. PORT=9697 for another server,
# INSTANCE=instance-dev for another instance.
cd "$(dirname "$0")/../${INSTANCE:-instance}"
PORT=${PORT:-9696}
echo "===== hmmrevive-server-$PORT.log"; cat hmmrevive-server-$PORT.log 2>/dev/null
echo "===== game log"; latest=$(ls -t logs/log-*.txt 2>/dev/null | head -1)
[ -n "$latest" ] && grep -E "^\[" "$latest" | grep -viE "shader|Performance\)" | cut -c1-260 | tail -${1:-40}
echo "===== unity exceptions"; grep -v "^\s*$\|Filename:" server_unity_$PORT.log 2>/dev/null | grep -B1 -A6 -E "Exception|ERROR: [^S]|crash" | grep -v "not supported on this GPU" | cut -c1-260 | head -${2:-40}
