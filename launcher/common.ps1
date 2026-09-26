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

function Load-Settings {
    $d = [ordered]@{ Arena = 1; Humans = 1; SameTeam = "y"; Score = 3; Car = "stingray"; BluBots = $null; RedBots = $null;
                     BluDifficulty = "auto"; RedDifficulty = "auto"; Name = "Player"; Ip = "127.0.0.1";
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
