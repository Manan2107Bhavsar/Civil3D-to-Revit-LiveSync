# Builds Release DLLs and zips them with the installer into dist\LiveSync-<version>.zip.
# Needs Civil 3D 2027 and Revit 2027 installed (the projects reference their DLLs).
# Usage: powershell -ExecutionPolicy Bypass -File tools\package-release.ps1 -Version 0.1.0

param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$name = "LiveSync-$Version"
$stage = Join-Path $root "dist\$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

# SkipDeploy: don't touch this machine's dev deploy folders / Revit manifest.
dotnet build (Join-Path $root 'src\Civil3dPlugin\LiveSync.Civil3d.csproj') -c Release -p:SkipDeploy=true -o (Join-Path $stage 'Civil3D')
if ($LASTEXITCODE) { throw 'Civil 3D plugin build failed' }
dotnet build (Join-Path $root 'src\RevitReceiver\LiveSync.Revit.csproj') -c Release -p:SkipDeploy=true -o (Join-Path $stage 'Revit')
if ($LASTEXITCODE) { throw 'Revit add-in build failed' }

# Keep only the DLLs (no pdb/json clutter for end users).
Get-ChildItem $stage -Recurse -File | Where-Object Extension -ne '.dll' | Remove-Item

Copy-Item (Join-Path $root 'installer\*') $stage
$zip = Join-Path $root "dist\$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $stage -DestinationPath $zip
Write-Host "Package: $zip" -ForegroundColor Green
