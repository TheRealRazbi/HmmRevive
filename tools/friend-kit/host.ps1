# Host a match on this PC: pick arena, humans and bots, start the server, tell players which IP to join.
# Remembers answers in host-settings.json. Points to win stay at 3 (play.bat doesn't pass a score to players).
#   -Port : for tests only; play.bat always joins 9696
param([int]$Port = 9696)
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
$exe = Join-Path $inst "HMM.exe"
if (-not (Test-Path $exe)) { throw "Run setup.bat first." }
$gameVersion = Get-ModVersion (Join-Path $inst "HMM_Data\Managed\HmmRevive.dll")
if ($kitVersion -and $gameVersion -ne $kitVersion) {
    Write-Host "This kit is $kitVersion but your game copy has $gameVersion. Run setup.bat again to update it." -ForegroundColor Yellow
}

function Ask($label, $current) {
    $v = Read-Host "$label [$current]"
    if ($v) { return $v.Trim() } else { return $current }
}
function Ask-Number($label, $current, $min, $max) {
    if ($null -eq $current -or [int]$current -lt $min -or [int]$current -gt $max) { $current = $max }
    while ($true) {
        $n = 0
        if ([int]::TryParse((Ask "$label ($min-$max)" $current), [ref]$n) -and $n -ge $min -and $n -le $max) { return $n }
        Write-Host "  enter a number from $min to $max"
    }
}
function Ask-Difficulty($label, $current) {
    $levels = @("easy", "medium", "hard", "auto")
    if ($levels -notcontains $current) { $current = "auto" }
    while ($true) {
        $v = (Ask "$label (easy/medium/hard/auto)" $current).ToLower()
        $hit = $levels | Where-Object { $_.StartsWith($v) } | Select-Object -First 1
        if ($v -and $hit) { return $hit }
        Write-Host "  enter easy, medium, hard or auto"
    }
}

# Firewall: players reach the server on UDP 9696, only from Tailscale addresses (100.64.0.0/10).
$ruleName = "HMM Revive server"
# Without admin, Get-NetFirewallRule is "access denied" (so the rule looked missing and we asked on every host), but
# netsh can read rules. Its text is localized, so only its exit code and the (unlocalized) program path are used.
function Test-FirewallRule {
    $out = netsh advfirewall firewall show rule name="$ruleName" verbose 2>$null | Out-String
    return ($LASTEXITCODE -eq 0) -and ($out -match [regex]::Escape($exe))
}
if (-not (Test-FirewallRule) -and (Ask "Players can't connect until Windows Firewall allows it. Add the rule now? (Windows asks for admin) (y/n)" "y") -eq "y") {
    $cmd = "Remove-NetFirewallRule -DisplayName '$ruleName' -ErrorAction SilentlyContinue; " +
        "New-NetFirewallRule -DisplayName '$ruleName' -Direction Inbound -Protocol UDP -LocalPort $Port " +
        "-RemoteAddress 100.64.0.0/10 -Program '$($exe -replace "'", "''")' -Action Allow | Out-Null"
    $enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cmd))
    try { Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile", "-EncodedCommand", $enc } catch {}
    if (Test-FirewallRule) { Write-Host "Firewall rule added." }
    else { Write-Host "Firewall rule was NOT added; players won't be able to connect." }
}

$running = Get-CimInstance Win32_Process -Filter "Name='HMM.exe'" |
    Where-Object { $_.ExecutablePath -eq $exe -and $_.CommandLine -match 'hmmrevive-server' }
if ($running) {
    if ((Ask "A server is already running. Stop it and start a new one? (y/n)" "y") -ne "y") { exit }
    $running | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    Start-Sleep -Milliseconds 700
}

$cfgFile = Join-Path $kit "host-settings.json"
$s = [pscustomobject]@{ Arena = 1; Humans = 2; SameTeam = "n"; BluBots = $null; RedBots = $null; BluDifficulty = "auto"; RedDifficulty = "auto" }
if (Test-Path $cfgFile) { $s = Get-Content $cfgFile -Raw | ConvertFrom-Json }

Write-Host ""
Write-Host "Arenas:"
Write-Host "   1  Legacy: Temple of Sacrifice"
Write-Host "   2  Metal God Arena"
Write-Host "   3  Cursed Necropolis"
Write-Host "   4  Sacrifice Sanctuary"
Write-Host "  13  Sacrifice Sanctuary (alpha version)"
Write-Host "   5  Arena Void (test map)"
$s.Arena = [int](Ask "Arena number" $s.Arena)
$s.Humans = Ask-Number "Human players (including you if you play)" $s.Humans 1 8
$s.SameTeam = if ($s.Humans -gt 1 -and $s.Humans -le 4) { Ask "All humans on the same team, against bots? (y/n)" $s.SameTeam } else { "n" }

# Up to 4 cars per team, each team needs at least one. Otherwise humans alternate Blue/Red in joining order.
if ($s.SameTeam -eq "y") { $blue = $s.Humans; $red = 0 } else { $blue = [math]::Ceiling($s.Humans / 2); $red = [math]::Floor($s.Humans / 2) }
Write-Host ""
$s.BluBots = Ask-Number "Blue team bots" $s.BluBots ([int]($blue -eq 0)) (4 - $blue)
if ($s.BluBots -gt 0) { $s.BluDifficulty = Ask-Difficulty "  their difficulty" $s.BluDifficulty }
$s.RedBots = Ask-Number "Red team bots" $s.RedBots ([int]($red -eq 0)) (4 - $red)
if ($s.RedBots -gt 0) { $s.RedDifficulty = Ask-Difficulty "  their difficulty" $s.RedDifficulty }
$s | ConvertTo-Json | Set-Content $cfgFile

Remove-Item (Join-Path $inst "hmmrevive-server-$Port.log"), (Join-Path $inst "server_unity_$Port.log") -ErrorAction SilentlyContinue
$a = @("-batchmode", "-nographics")
if ($s.BluBots -gt 0 -and $s.BluDifficulty -ne "auto") { $a += "--hmmrevive-difficulty-blue=$($s.BluDifficulty)" }
if ($s.RedBots -gt 0 -and $s.RedDifficulty -ne "auto") { $a += "--hmmrevive-difficulty-red=$($s.RedDifficulty)" }
# --Drafter=0: character-select config by id; the by-team-size lookup throws on anything but 4v4.
$a += @("-logFile", "`"$(Join-Path $inst "server_unity_$Port.log")`"", "--hmmrevive-server", "--Drafter=0",
    "BeginConfig",
    "[Debug]", "SkipSwordfish=true", "IsDebug=true",
    "[Game]", "PlayerCount=$($s.Humans)", "ArenaIndex=$($s.Arena)", "RedTeamBotsCount=$($s.RedBots)", "BluTeamBotsCount=$($s.BluBots)",
    "AllPlayersOnBluTeam=$(if ($s.SameTeam -eq 'y') { 'true' } else { 'false' })",
    "[Server]", "Port=$Port",
    "EndConfig")
$p = Start-Process -FilePath $exe -ArgumentList $a -WorkingDirectory $inst -WindowStyle Hidden -PassThru
Start-Sleep -Milliseconds 300
try { $p.PriorityClass = "BelowNormal" } catch {}

$ts = Get-Command tailscale -ErrorAction SilentlyContinue
if (-not $ts) { $ts = Get-Command "$env:ProgramFiles\Tailscale\tailscale.exe" -ErrorAction SilentlyContinue }
$ip = if ($ts) { & $ts.Source ip -4 2>$null | Select-Object -First 1 } else { "(Tailscale not found)" }
Write-Host ""
Write-Host "Server starting (about 20 s). It waits for $($s.Humans) player(s), then the match begins."
Write-Host "Players run play.bat and type this IP: $ip"
Write-Host "After the match, run host.bat again for the next one. stop.bat stops the server."
Write-Host ""
if ((Ask "Play in this match yourself? (y/n)" "y") -eq "y") { & (Join-Path $kit "play.ps1") -Ip 127.0.0.1 }
