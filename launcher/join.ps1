# Join a match (your own server by default): pick your car from a menu. Remembers answers in settings.json.
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")
$s = Load-Settings

Write-Host ""
Write-Host "Cars:"
for ($i = 0; $i -lt $Cars.Count; $i += 2) {
    $line = "  {0,2}  {1,-18}" -f $Cars[$i].Id, $Cars[$i].Name
    if ($i + 1 -lt $Cars.Count) { $line += "  {0,2}  {1}" -f $Cars[$i + 1].Id, $Cars[$i + 1].Name }
    Write-Host $line
}
$car = Ask "Car number or name" $s.Car
$s.Car = $car -replace '["'']', ''
$skin = Ask-Skin (Join-Path $PSScriptRoot "skins.txt") $s.Car $s
$s.Name = (Ask "Your name" $s.Name) -replace '[#\s]', ''
$s.Ip = Ask "Server IP or name (127.0.0.1 = this PC, or the host's Tailscale IP / relay address)" $s.Ip
Save-Settings $s

$params = @{ Name = $s.Name; Ip = $s.Ip; Car = $s.Car; Skin = $skin; Width = $s.Width; Height = $s.Height }
if ($s.Fullscreen) { $params.Fullscreen = $true } else { $params.Windowed = $true }
if ($s.Ip -eq "127.0.0.1" -and $s.Score -ne 3) { $params.Score = $s.Score } # same points-to-win as the server host.bat started
& (Join-Path $Repo "tools\run_client.ps1") @params
