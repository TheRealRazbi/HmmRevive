# Stop the server and any game started from this kit's instance (never touches the Steam install).
$inst = Join-Path $PSScriptRoot "instance"
$procs = @(Get-CimInstance Win32_Process -Filter "Name='HMM.exe'" | Where-Object { $_.ExecutablePath -like "$inst\*" })
if ($procs.Count -eq 0) { Write-Host "Nothing running."; exit }
foreach ($p in $procs) {
    $what = if ($p.CommandLine -match 'hmmrevive-server') { "server" } elseif ($p.CommandLine -match 'PlayerName=(\S+)') { "game ($($matches[1]))" } else { "HMM" }
    Write-Host "  $($p.ProcessId)  $what"
}
if ((Read-Host "Stop all of these? (y/n) [y]") -notin @("", "y")) { exit }
$procs | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Write-Host "Stopped."
