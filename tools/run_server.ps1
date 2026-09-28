# Launch a headless HMM match server from ./instance (built by tools/build.sh).
param(
    [int]$Port = 9696,
    [int]$Players = 1,
    [int]$Arena = 1,
    [int]$RedBots = 0,
    [int]$BluBots = 0,
    [string]$RedDifficulty = "auto", # bot AI per team: easy, medium, hard or auto (game's own pick)
    [string]$BluDifficulty = "auto",
    [string]$RedBotCars = "", # bot cars in slot order: "6,wildfire,random", or "random" for all; empty = game default
    [string]$BluBotCars = "",
    [switch]$SameTeam, # all humans on Blue (then use -BluBots 4-humans); default alternates humans Red/Blue
    [int]$Score = 0,   # points to win (0 = default 3); pass the same -Score to clients
    [switch]$Graphics, # run with a GPU device (no -nographics) if headless mode breaks something
    [switch]$Chaos,    # test mode: every car switches to a random car on each death and each round
    [string]$Repair = "", # out-of-combat repair: "off", or "DELAY,PERCENT" (default 5,7 = 7% max HP/s after 5 s unhit)
    [int]$EndQuit = -1, # seconds the server stays up after the match ends (mod default 30; 0 = forever)
    [string]$ChaosCars = "", # -Chaos limited to these car ids, e.g. "8,7" swaps Windrider <-> Full Metal Judge
    [string]$Instance = "instance" # game instance folder in the repo (e.g. instance-dev built with HMM_INSTANCE=instance-dev)
)
$inst = Join-Path $PSScriptRoot "..\$Instance" | Resolve-Path
# logs are per port (hmmrevive-server-<port>.log, server_unity_<port>.log) so a second server can't clobber them
Remove-Item "$inst\hmmrevive-server-$Port.log", "$inst\server_unity_$Port.log" -ErrorAction SilentlyContinue
$a = @("-batchmode")
if (-not $Graphics) { $a += "-nographics" }
if ($Score -gt 0) { $a += "--hmmrevive-score=$Score" }
if ($Chaos) { $a += "--hmmrevive-chaos" }
if ($EndQuit -ge 0) { $a += "--hmmrevive-end-quit=$EndQuit" }
if ($Repair) { $a += "--hmmrevive-repair=$($Repair -replace '\s','')" }
if ($ChaosCars) { $a += "--hmmrevive-chaos-cars=$($ChaosCars -replace '\s','')" }
if ($RedBotCars) { $a += "--hmmrevive-bot-cars-red=$($RedBotCars -replace '\s','')" }
if ($BluBotCars) { $a += "--hmmrevive-bot-cars-blue=$($BluBotCars -replace '\s','')" }
if ($RedDifficulty -ne "auto") { $a += "--hmmrevive-difficulty-red=$RedDifficulty" }
if ($BluDifficulty -ne "auto") { $a += "--hmmrevive-difficulty-blue=$BluDifficulty" }
# --Drafter=0: character-select config by id (Casual) instead of by team size; the game only has size-4 entries and
# throws on uneven or smaller teams. For 4v4 it is the same config the size lookup finds.
$a += @("-logFile", "$inst\server_unity_$Port.log", "--hmmrevive-server", "--Drafter=0",
    "BeginConfig",
    "[Debug]", "SkipSwordfish=true", "IsDebug=true",
    "[Game]", "PlayerCount=$Players", "ArenaIndex=$Arena", "RedTeamBotsCount=$RedBots", "BluTeamBotsCount=$BluBots", "AllPlayersOnBluTeam=$(([string]$SameTeam.IsPresent).ToLower())",
    "[Server]", "Port=$Port",
    "EndConfig")
$p = Start-Process -FilePath "$inst\HMM.exe" -ArgumentList $a -WorkingDirectory $inst -PassThru
Start-Sleep -Milliseconds 300
try { $p.PriorityClass = "BelowNormal" } catch {}
Set-Content "$inst\server.pid" $p.Id
"server PID=$($p.Id) port=$Port players=$Players arena=$Arena bots=$RedBots/$BluBots ($RedDifficulty/$BluDifficulty)"
