# Builds Release DLLs for Civil 3D / Revit 2025, 2026 and 2027 and zips them with the installer
# into dist\LiveSync-<version>.zip. Needs Civil 3D 2027 and Revit 2027 installed (the 2027 builds
# reference their DLLs; 2025/2026 use Autodesk's NuGet reference packages).
# Usage: powershell -ExecutionPolicy Bypass -File tools\package-release.ps1 -Version 0.2.0

param([string]$Version = '0.2.0')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$name = "LiveSync-$Version"
$stage = Join-Path $root "dist\$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

# One build per year (2025 and 2026 are .NET 8, 2027 is .NET 10).
# SkipDeploy: don't touch this machine's dev deploy folders / Revit manifests.
foreach ($year in 2025, 2026, 2027) {
    dotnet build (Join-Path $root 'src\Civil3dPlugin\LiveSync.Civil3d.csproj') -c Release -p:HostYear=$year -p:SkipDeploy=true -o (Join-Path $stage "Civil3D\$year")
    if ($LASTEXITCODE) { throw "Civil 3D $year plugin build failed" }
    dotnet build (Join-Path $root 'src\RevitReceiver\LiveSync.Revit.csproj') -c Release -p:HostYear=$year -p:SkipDeploy=true -o (Join-Path $stage "Revit\$year")
    if ($LASTEXITCODE) { throw "Revit $year add-in build failed" }
}

# Keep only the DLLs (no pdb/json clutter for end users).
Get-ChildItem $stage -Recurse -File | Where-Object Extension -ne '.dll' | Remove-Item

Copy-Item (Join-Path $root 'installer\*') $stage
$zip = Join-Path $root "dist\$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $stage -DestinationPath $zip
Write-Host "Package: $zip" -ForegroundColor Green
