# Stop every server and game window started from this project's instance (never touches the Steam install).
. (Join-Path $PSScriptRoot "common.ps1")
$procs = @(Get-HmmProcesses)
if ($procs.Count -eq 0) { Write-Host "Nothing running."; exit }
foreach ($p in $procs) {
    $what = if ($p.CommandLine -match 'hmmrevive-server') { "server" } elseif ($p.CommandLine -match 'PlayerName=(\S+)') { "game ($($matches[1]))" } else { "HMM" }
    Write-Host "  $($p.ProcessId)  $what"
}
if ((Ask "Stop all of these? (y/n)" "y") -ne "y") { exit }
$procs | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Write-Host "Stopped."
