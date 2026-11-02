# Builds the portable OrixNotch.exe (self-contained single file, no install).
# Run from repo root:  pwsh packaging/Build-Exe.ps1 [-Version 1.2.3]
param([string]$Version, [string]$OutDir)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot/Common.ps1"

$appVersion = Get-AppVersion $Version
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot "publish/exe" }
Write-Host "Portable exe $appVersion"

Invoke-Publish -OutDir $OutDir -Version $appVersion -SingleFile
$mainExe = Get-ChildItem $OutDir -Filter *.exe | Select-Object -First 1
if ($mainExe -and $mainExe.Name -ne "NekoNotch.exe") {
    Copy-Item $mainExe.FullName (Join-Path $OutDir "NekoNotch.exe") -Force
}
$exe = Join-Path $OutDir "NekoNotch.exe"
Invoke-CodeSign $exe

Write-Host "Exe ready: $exe"
Copy-ToOutput $exe "NekoNotch.exe"
