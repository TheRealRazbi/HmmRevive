# Join a Heavy Metal Machines match hosted by a friend. Remembers your answers in play-settings.json.
#   -Ip : join this address without asking or saving it (host.bat passes 127.0.0.1 for the host's own game)
param([string]$Ip = "")
$ErrorActionPreference = "Stop"
$kit = $PSScriptRoot
# Version (MAJOR.MINOR) the mod DLL was built with, printed first so screenshots and videos show it.
# Builds from before versioning report "1.0.0+<commit>": those are "pre-1.0".
function Get-ModVersion($dll) {
    if (-not (Test-Path $dll)) { return $null }
    $v = [Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $dll).Path).ProductVersion
    if ($v -match '^\d+\.\d+$') { return $v } else { return "pre-1.0" }
}
$kitVersion = Get-ModVersion (Join-Path $kit "mod\HmmRevive.dll")
Write-Host "HMM Revive $kitVersion"
# The game's native logger can't open files under a path with non-ASCII letters (e.g. a Windows user name with an accented or Turkish letter)
# and the game crashes right at start, so stop early with a clear message instead.
if ($kit -match '[^\x20-\x7E]') {
    throw ("The game can't start from a folder whose path has special letters: $kit`n" +
        "Move this whole kit folder to a plain path, e.g. C:\HMM-Revive, and run it from there (no need to run setup again).`n" +
        "O jogo nao abre numa pasta cujo caminho tem letras especiais. Mova esta pasta do kit para, por exemplo, C:\HMM-Revive e rode de la.")
}
$inst = Join-Path $kit "instance"
if (-not (Test-Path (Join-Path $inst "HMM.exe"))) { throw "Run setup.bat first." }
$gameVersion = Get-ModVersion (Join-Path $inst "HMM_Data\Managed\HmmRevive.dll")
if ($kitVersion -and $gameVersion -ne $kitVersion) {
    Write-Host "This kit is $kitVersion but your game copy has $gameVersion. Run setup.bat again to update it." -ForegroundColor Yellow
}

$cfgFile = Join-Path $kit "play-settings.json"
$cfg = [pscustomobject]@{ Ip = ""; Name = $env:USERNAME; Car = "" }
if (Test-Path $cfgFile) { $cfg = Get-Content $cfgFile -Raw | ConvertFrom-Json }
function Ask($label, $current) {
    $v = Read-Host "$label [$current]"
    if ($v) { return $v.Trim() } else { return $current }
}
if (-not $Ip) { $cfg.Ip = Ask "Host address (Tailscale 100.x IP, or a relay address the host gave you)" $cfg.Ip }
$cfg.Name = (Ask "Your name (must differ from the other players)" $cfg.Name) -replace '[#\s]', ''
Write-Host "Cars: Stingray, Black Lotus, Artificer, Rampage, Dirt Devil, Wildfire, Full Metal Judge, Windrider,"
Write-Host "      Icebringer, Metal Herald, Little Monster, Clunker, Stargazer, Peacemaker, Vulture, Calamity, Photon, Killer J."
$cfg.Car = (Ask "Car (blank = default)" $cfg.Car) -replace '["'']', ''
$cfg | ConvertTo-Json | Set-Content $cfgFile
if (-not $Ip) { $Ip = $cfg.Ip }

$a = @("-logFile", "`"$(Join-Path $inst "client_$($cfg.Name).log")`"")
if ($cfg.Car) { $a += "--hmmrevive-car=$($cfg.Car -replace '\s', '')" }
$a += @("BeginConfig",
    "[Debug]", "SkipSwordfish=true", "DirectMatch=true", "PlayerName=$($cfg.Name)",
    "[Server]", "IP=$Ip", "Port=9696",
    "EndConfig")
Start-Process -FilePath (Join-Path $inst "HMM.exe") -ArgumentList $a -WorkingDirectory $inst
Write-Host "Starting the game... (the host must have started the server first)"
