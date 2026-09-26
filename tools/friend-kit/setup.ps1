# One-time setup: finds your Heavy Metal Machines install and builds a patched copy in .\instance.
# Your Steam install is never modified (files are hardlinked, or copied if the game is on another drive).
$ErrorActionPreference = "Stop"
$kit = $PSScriptRoot
# Version (MAJOR.MINOR) the mod DLL was built with, printed first so screenshots and videos show it.
# Builds from before versioning report "1.0.0+<commit>": those are "pre-1.0".
function Get-ModVersion($dll) {
    if (-not (Test-Path $dll)) { return $null }
    $v = [Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $dll).Path).ProductVersion
    if ($v -match '^\d+\.\d+$') { return $v } else { return "pre-1.0" }
}
Write-Host "HMM Revive $(Get-ModVersion (Join-Path $kit 'mod\HmmRevive.dll'))"
# The game's native logger can't open files under a path with non-ASCII letters (e.g. a Windows user name with an accented or Turkish letter)
# and the game crashes right at start, so stop early with a clear message instead.
if ($kit -match '[^\x20-\x7E]') {
    throw ("The game can't start from a folder whose path has special letters: $kit`n" +
        "Move this whole kit folder to a plain path, e.g. C:\HMM-Revive, and run it from there (no need to run setup again).`n" +
        "O jogo nao abre numa pasta cujo caminho tem letras especiais. Mova esta pasta do kit para, por exemplo, C:\HMM-Revive e rode de la.")
}
if (-not (Test-Path (Join-Path $kit "Patcher.exe"))) {
    throw "Patcher.exe is missing next to setup.bat. Run the packaged kit (the unzipped HMM-Revive-friend-kit-<version>.zip, or build/friend-kit), not tools/friend-kit, which only holds its source scripts."
}
$candidates = @()
$steam = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
if ($steam) {
    $candidates += Join-Path $steam "steamapps\common\Heavy Metal Machines"
    $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
    if (Test-Path $vdf) {
        foreach ($m in Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"') {
            $lib = $m.Matches[0].Groups[1].Value -replace '\\\\', '\'
            $candidates += Join-Path $lib "steamapps\common\Heavy Metal Machines"
        }
    }
}
$game = $candidates | Where-Object { Test-Path (Join-Path $_ "HMM.exe") } | Select-Object -First 1
if (-not $game) {
    $game = Read-Host "Heavy Metal Machines folder (Steam > right-click the game > Manage > Browse local files)"
}
if (-not (Test-Path (Join-Path $game "HMM.exe"))) { throw "HMM.exe not found in '$game'" }
Write-Host "Game found: $game"
& (Join-Path $kit "Patcher.exe") $game (Join-Path $kit "instance") (Join-Path $kit "mod")
if ($LASTEXITCODE -ne 0) { throw "patcher failed (exit $LASTEXITCODE)" }
Write-Host ""
Write-Host "Setup done. Run play.bat to join a game, or host.bat to host one."
