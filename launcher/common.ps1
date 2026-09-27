# Shared helpers and lists for the launcher scripts.
$Repo = Split-Path $PSScriptRoot -Parent
$SettingsFile = Join-Path $PSScriptRoot "settings.json"

# Car ids as the game numbers them (server log: "cars: 1=Doppler/Stingray, ...").
$Cars = @(
    @{ Id = 1; Name = "Stingray" }, @{ Id = 2; Name = "Black Lotus" }, @{ Id = 3; Name = "Artificer" },
    @{ Id = 4; Name = "Rampage" }, @{ Id = 5; Name = "Dirt Devil" }, @{ Id = 6; Name = "Wildfire" },
    @{ Id = 7; Name = "Full Metal Judge" }, @{ Id = 8; Name = "Windrider" }, @{ Id = 9; Name = "Icebringer" },
    @{ Id = 10; Name = "Metal Herald" }, @{ Id = 13; Name = "Little Monster" }, @{ Id = 14; Name = "Clunker" },
    @{ Id = 15; Name = "Stargazer" }, @{ Id = 16; Name = "Peacemaker" }, @{ Id = 17; Name = "Vulture" },
    @{ Id = 18; Name = "Calamity" }, @{ Id = 19; Name = "Photon" }, @{ Id = 20; Name = "Killer J." }
)

# Arena indexes from the game's GameArenaConfig (server log: "arenas: ..."). Custom/Competitive variants share a map.
$Arenas = @(
    @{ Id = 1; Name = "Legacy: Temple of Sacrifice" },
    @{ Id = 2; Name = "Metal God Arena" },
    @{ Id = 3; Name = "Cursed Necropolis" },
    @{ Id = 4; Name = "Sacrifice Sanctuary" },
    @{ Id = 13; Name = "Sacrifice Sanctuary (alpha version)" },
    @{ Id = 5; Name = "Arena Void (test map)" }
)

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

# Whole number in [min, max]. A saved value that no longer fits (e.g. more humans now) falls back to max.
function Ask-Number($label, $current, $min, $max) {
    if ($null -eq $current -or [int]$current -lt $min -or [int]$current -gt $max) { $current = $max }
    while ($true) {
        $n = 0
        if ([int]::TryParse((Ask "$label ($min-$max)" $current), [ref]$n) -and $n -ge $min -and $n -le $max) { return $n }
        Write-Host "  enter a number from $min to $max"
    }
}

# Bot AI levels (auto = the game's own pick from player MMR).
$Difficulties = @("easy", "medium", "hard", "auto")

function Ask-Difficulty($label, $current) {
    if ($Difficulties -notcontains $current) { $current = "auto" }
    while ($true) {
        $v = (Ask "$label (easy/medium/hard/auto)" $current).ToLower()
        $hit = $Difficulties | Where-Object { $_.StartsWith($v) } | Select-Object -First 1
        if ($v -and $hit) { return $hit }
        Write-Host "  enter easy, medium, hard or auto"
    }
}

# Bot cars in slot order: car names or numbers separated by commas ("wildfire, photon, 16"), "random" (a different
# random car each) or "default" (the game's usual line-up). Checked against the car list in skins.txt.
function Ask-BotCars($label, $current, $carsFile) {
    if (-not $current) { $current = "default" }
    $cars = @()
    if (Test-Path $carsFile) {
        $cars = @(Get-Content $carsFile | Where-Object { $_ } | ForEach-Object { $f = $_ -split "`t"; "$($f[0]) $($f[1])" } | Select-Object -Unique)
    }
    while ($true) {
        $v = ((Ask "$label (car names or numbers separated by commas, random, or default)" $current) -replace '["'']', '').Trim()
        $bad = @()
        foreach ($e in $v -split ',') {
            $k = ($e -replace "[\s'._]", '').ToLower()
            if (-not $k -or $k -eq 'random' -or $k -eq 'default' -or $cars.Count -eq 0) { continue }
            $hit = $cars | Where-Object { $id, $name = $_ -split ' ', 2; $id -eq $k -or ($name -replace "[\s'._]", '').ToLower().Contains($k) }
            if (-not $hit) { $bad += $e.Trim() }
        }
        if ($bad.Count -eq 0) { return $v }
        Write-Host "  unknown car: $($bad -join ', '). Cars: $(($cars | ForEach-Object { ($_ -split ' ', 2)[1] }) -join ', ')"
    }
}

function Load-Settings {
    $d = [ordered]@{ Arena = 1; Humans = 1; SameTeam = "y"; Score = 3; Car = "stingray"; Skins = [pscustomobject]@{}; BluBots = $null; RedBots = $null;
                     BluDifficulty = "auto"; RedDifficulty = "auto"; BluBotCars = "default"; RedBotCars = "default"; Name = "Player"; Ip = "127.0.0.1";
                     RelayAddress = ""; Width = 2560; Height = 1440; Fullscreen = $true }
    if (Test-Path $SettingsFile) {
        $saved = Get-Content $SettingsFile -Raw | ConvertFrom-Json
        foreach ($p in $saved.PSObject.Properties) { $d[$p.Name] = $p.Value }
    }
    return [pscustomobject]$d
}

function Save-Settings($s) { $s | ConvertTo-Json | Set-Content $SettingsFile }

# HMM.exe processes started from this repo's instance (not the Steam install).
function Get-HmmProcesses {
    Get-CimInstance Win32_Process -Filter "Name='HMM.exe'" | Where-Object { $_.ExecutablePath -like "$Repo\instance\*" }
}

function Get-TailscaleIp {
    try { (& tailscale ip -4 2>$null | Select-Object -First 1) } catch { "(Tailscale not found)" }
}
