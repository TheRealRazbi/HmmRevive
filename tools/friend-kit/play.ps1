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
# Skins: skins.txt lists each car's skins as "carId<TAB>car<TAB>number<TAB>skin" (0 = the original look).
# Show-Skins prints the list for a car number or name and returns the car's name ($null if unknown).
function Show-Skins($file, $car) {
    if (-not $car -or -not (Test-Path $file)) { return $null }
    $rows = @(Get-Content $file | Where-Object { $_ } | ForEach-Object {
        $f = $_ -split "`t"; [pscustomobject]@{ Id = $f[0]; Car = $f[1]; N = [int]$f[2]; Skin = $f[3] } })
    $key = ($car -replace "[\s'._]", '').ToLower()
    $mine = @($rows | Where-Object { $_.Id -eq $key -or ($_.Car -replace "[\s'._]", '').ToLower() -eq $key })
    if ($mine.Count -eq 0) { $mine = @($rows | Where-Object { ($_.Car -replace "[\s'._]", '').ToLower().Contains($key) }) }
    if ($mine.Count -eq 0) { return $null }
    $mine = @($mine | Where-Object { $_.Id -eq $mine[0].Id })
    Write-Host "Skins for $($mine[0].Car):"
    $half = [math]::Ceiling($mine.Count / 2)
    for ($i = 0; $i -lt $half; $i++) {
        $l = $mine[$i]; $line = "  {0,2}  {1,-36}" -f $l.N, $l.Skin
        if ($i + $half -lt $mine.Count) { $r = $mine[$i + $half]; $line += "  {0,2}  {1}" -f $r.N, $r.Skin }
        Write-Host $line
    }
    return $mine[0].Car
}

# Asks for the skin of $car, remembered per car in $settings.Skins ({ "Wildfire": "3" }; "0" for a car not set
# yet), and returns it.
function Ask-Skin($file, $car, $settings) {
    if (-not ($settings.PSObject.Properties.Name -contains "Skins") -or $null -eq $settings.Skins) {
        $settings | Add-Member -NotePropertyName Skins -NotePropertyValue ([pscustomobject]@{}) -Force
    }
    $settings.PSObject.Properties.Remove("Skin") # one skin for every car, before 1.1
    $name = Show-Skins $file $car
    $key = if ($name) { $name } else { "other" }
    $current = $settings.Skins.$key
    if (-not $current) { $current = "0" }
    $skin = (Ask "Skin: number, name, or random (0 = original look; in a match, /skins lists them)" $current) -replace '["''#]', ''
    $settings.Skins | Add-Member -NotePropertyName $key -NotePropertyValue $skin -Force
    return $skin
}

function Ask($label, $current) {
    $v = Read-Host "$label [$current]"
    if ($v) { return $v.Trim() } else { return $current }
}
if (-not $Ip) { $cfg.Ip = Ask "Host address (Tailscale 100.x IP, or a relay address the host gave you)" $cfg.Ip }
$cfg.Name = (Ask "Your name (must differ from the other players)" $cfg.Name) -replace '[#\s]', ''
Write-Host "Cars: Stingray, Black Lotus, Artificer, Rampage, Dirt Devil, Wildfire, Full Metal Judge, Windrider,"
Write-Host "      Icebringer, Metal Herald, Little Monster, Clunker, Stargazer, Peacemaker, Vulture, Calamity, Photon, Killer J."
$cfg.Car = (Ask "Car (blank = default)" $cfg.Car) -replace '["'']', ''
$skin = Ask-Skin (Join-Path $kit "skins.txt") $cfg.Car $cfg
$cfg | ConvertTo-Json | Set-Content $cfgFile
if (-not $Ip) { $Ip = $cfg.Ip }

$a = @("-logFile", "`"$(Join-Path $inst "client_$($cfg.Name).log")`"")
if ($cfg.Car) { $a += "--hmmrevive-car=$($cfg.Car -replace '\s', '')" }
if ($skin -and $skin -ne "0") { $a += "--hmmrevive-skin=$($skin -replace '\s', '')" }
$a += @("BeginConfig",
    "[Debug]", "SkipSwordfish=true", "DirectMatch=true", "PlayerName=$($cfg.Name)",
    "[Server]", "IP=$Ip", "Port=9696",
    "EndConfig")
Start-Process -FilePath (Join-Path $inst "HMM.exe") -ArgumentList $a -WorkingDirectory $inst
Write-Host "Starting the game... (the host must have started the server first)"
