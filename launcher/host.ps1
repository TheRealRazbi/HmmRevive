# Start a match server: pick arena, humans, teams and score from menus. Remembers answers in settings.json.
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")
$s = Load-Settings

if (Get-HmmProcesses | Where-Object { $_.CommandLine -match 'hmmrevive-server' }) {
    if ((Ask "A server is already running. Stop it and start a new one? (y/n)" "y") -ne "y") { exit }
    Get-HmmProcesses | Where-Object { $_.CommandLine -match 'hmmrevive-server' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
    Start-Sleep -Milliseconds 700
}

Write-Host ""
Write-Host "Arenas:"
foreach ($a in $Arenas) { Write-Host ("  {0,2}  {1}" -f $a.Id, $a.Name) }
$arena = [int](Ask "Arena number" $s.Arena)

$humans = [int](Ask "Human players (including you)" $s.Humans)
$same = if ($humans -gt 1) { (Ask "All humans on the same team? (y/n)" $s.SameTeam) } else { "y" }
$score = [int](Ask "Points to win (3 = normal, 1 = quick)" $s.Score)

# Up to 4 cars per team, each team needs at least one. Without -SameTeam humans alternate, starting on Blue (you).
if ($same -eq "y") { $blueHumans = $humans; $redHumans = 0 }
else { $blueHumans = [math]::Ceiling($humans / 2); $redHumans = [math]::Floor($humans / 2) }
if ($redHumans -eq 0) { $bluName = "Ally bots (your team, Blue)"; $redName = "Enemy bots (Red)" }
else { $bluName = "Blue team bots"; $redName = "Red team bots" }
Write-Host ""
$bluBots = Ask-Number $bluName $s.BluBots ([int]($blueHumans -eq 0)) (4 - $blueHumans)
$bluDiff = if ($bluBots -gt 0) { Ask-Difficulty "  their difficulty" $s.BluDifficulty } else { $s.BluDifficulty }
$redBots = Ask-Number $redName $s.RedBots ([int]($redHumans -eq 0)) (4 - $redHumans)
$redDiff = if ($redBots -gt 0) { Ask-Difficulty "  their difficulty" $s.RedDifficulty } else { $s.RedDifficulty }

$params = @{ Players = $humans; Arena = $arena; RedBots = $redBots; BluBots = $bluBots; RedDifficulty = $redDiff; BluDifficulty = $bluDiff }
if ($same -eq "y" -and $humans -gt 1) { $params.SameTeam = $true }
if ($score -ne 3) { $params.Score = $score }

$s.Arena = $arena; $s.Humans = $humans; $s.SameTeam = $same; $s.Score = $score
$s.BluBots = $bluBots; $s.RedBots = $redBots; $s.BluDifficulty = $bluDiff; $s.RedDifficulty = $redDiff
Save-Settings $s

& (Join-Path $Repo "tools\run_server.ps1") @params
$name = ($Arenas | Where-Object { $_.Id -eq $arena } | Select-Object -First 1).Name
Write-Host "Server started: $name, $humans human(s), bots Blue=$bluBots ($bluDiff) Red=$redBots ($redDiff)."
Write-Host "Friends join with IP $(Get-TailscaleIp) (Tailscale)."
# Optional public relay: a WireGuard tunnel service "hmm-relay" to a server that forwards UDP 9696 here, and its
# address in settings.json ("RelayAddress").
if ($s.RelayAddress -and (Get-Service "WireGuardTunnel`$hmm-relay" -ErrorAction SilentlyContinue).Status -eq "Running") {
    Write-Host "Relay is on: anyone can also join with $($s.RelayAddress) (no Tailscale)."
}
if ($score -ne 3) { Write-Host "Note: points to win = $score. Your own client gets it automatically via join.bat." }

if ((Ask "Join the match now? (y/n)" "y") -eq "y") { & (Join-Path $PSScriptRoot "join.ps1") }
