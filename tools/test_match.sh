#!/usr/bin/env bash
# End-to-end smoke test: headless server + N hidden/muted clients on localhost, then dump logs.
#   tools/test_match.sh [clients=1] [seconds=60] [redBots=4] [bluBots=3]
#   INSTANCE=instance-dev PORT=9797 tools/test_match.sh ...   test in a second instance while ./instance is in use
#   SERVER_ARGS="-Chaos -Score 2" CLIENT_ARGS="-Car wildfire"  extra run_server.ps1 / run_client.ps1 arguments
cd "$(dirname "$0")/.."
N=${1:-1}; SECS=${2:-60}; RB=${3:-4}; BB=${4:-3}
INST=${INSTANCE:-instance}; PORT=${PORT:-9696}
KILL="\$exe = [IO.Path]::GetFullPath('$INST\HMM.exe'); Get-Process HMM -EA SilentlyContinue | ? { \$_.Path -eq \$exe } | Stop-Process -Force"
powershell -NoProfile -Command "$KILL" >/dev/null 2>&1
rm -f "$INST"/hmmrevive-*.log "$INST"/client_*.log "$INST"/client_*.pid
powershell -NoProfile -File tools/run_server.ps1 -Instance "$INST" -Port "$PORT" -Players "$N" -RedBots "$RB" -BluBots "$BB" $SERVER_ARGS
spid=$(cat "$INST/server.pid")
for i in $(seq 1 30); do netstat -ano -p UDP | grep -q ":$PORT .*$spid" && break; sleep 2; done
netstat -ano -p UDP | grep -q ":$PORT .*$spid" || { echo "SERVER NOT LISTENING"; INSTANCE=$INST PORT=$PORT tools/serverlog.sh 20 40; exit 1; }
echo "server listening"
for c in $(seq 1 "$N"); do powershell -NoProfile -File tools/run_client.ps1 -Instance "$INST" -Port "$PORT" -Name "Tester$c" -Background $CLIENT_ARGS; done
sleep "$SECS"
echo "===== alive after ${SECS}s:"; tasklist //FI "IMAGENAME eq HMM.exe" | grep HMM
INSTANCE=$INST PORT=$PORT tools/serverlog.sh 60 30
for f in "$INST"/hmmrevive-client-*.log; do echo "===== $f"; grep -v "HmmRevive init" "$f" | cut -c1-220 | head -30; done
powershell -NoProfile -Command "$KILL" >/dev/null 2>&1; echo "(instance processes stopped)"
