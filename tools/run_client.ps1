# Launch an HMM client from ./instance in Hoplon's dev direct-connect mode (no backend).
#   -Background : no window (-batchmode) + no audio, for automated tests while you use the PC
#   -Width/-Height : force a resolution (Unity -screen-width/-height); -Windowed / -Fullscreen force the mode.
#                    Otherwise the game uses its saved settings (changeable in the ESC > options menu).
param(
    [string]$Name = "Player$(Get-Random -Maximum 999)",
    [string]$Ip = "127.0.0.1",
    [int]$Port = 9696,
    [switch]$Background,
    [int]$Width = 0,
    [int]$Height = 0,
    [switch]$Windowed,
    [switch]$Fullscreen,
    [int]$Score = 0,   # must match the server's -Score
    [string]$Car = "", # car name or number, e.g. -Car stingray (see "cars:" in the server log); empty = default
    [string]$Skin = "", # skin number (launcher/skins.txt, 0 = default), name or "random"; empty = default
    [string]$Team = "", # red or blue: ask the server for this team (the server honours it); empty = the server's choice
    [string]$Instance = "instance", # game instance folder in the repo (e.g. instance-dev built with HMM_INSTANCE=instance-dev)
    [int]$Shots = 0,        # tests: N off-screen pictures of the match (hmmrevive-shot-<pid>-<n>.png next to HMM.exe)
    [string]$AutoChat = ""  # tests: chat lines sent 3 s into the match, ';'-separated, e.g. "/cars;/car wildfire"
)
$inst = Join-Path $PSScriptRoot "..\$Instance" | Resolve-Path
$log = "$inst\client_$Name.log"
Remove-Item $log -ErrorAction SilentlyContinue
$a = @()
if ($Background) { $a += @("-batchmode", "--hmmrevive-mute") }
if ($Score -gt 0) { $a += "--hmmrevive-score=$Score" }
if ($Car) { $a += "--hmmrevive-car=$($Car -replace '\s','')" }
if ($Skin) { $a += "--hmmrevive-skin=$($Skin -replace '\s','')" }
if ($Team) { $a += "--hmmrevive-team=$Team" }
if ($Shots -gt 0) { $a += "--hmmrevive-shots=$Shots" }
if ($AutoChat) { $a += "`"--hmmrevive-autochat=$AutoChat`"" }
if ($Width -gt 0 -and $Height -gt 0) { $a += @("-screen-width", $Width, "-screen-height", $Height) }
if ($Windowed) { $a += @("-screen-fullscreen", "0") } elseif ($Fullscreen) { $a += @("-screen-fullscreen", "1") }
$a += @("-logFile", $log,
    "BeginConfig",
    "[Debug]", "SkipSwordfish=true", "DirectMatch=true", "PlayerName=$Name",
    "[Server]", "IP=$Ip", "Port=$Port",
    "EndConfig")
# -batchmode still creates a blank (white) window; start it hidden so background tests stay invisible.
$style = if ($Background) { "Hidden" } else { "Normal" }
$p = Start-Process -FilePath "$inst\HMM.exe" -ArgumentList $a -WorkingDirectory $inst -WindowStyle $style -PassThru
if ($Background) { Start-Sleep -Milliseconds 300; try { $p.PriorityClass = "BelowNormal" } catch {} }
Set-Content "$inst\client_$Name.pid" $p.Id
"client '$Name' PID=$($p.Id) -> $($Ip):$Port background=$Background"
