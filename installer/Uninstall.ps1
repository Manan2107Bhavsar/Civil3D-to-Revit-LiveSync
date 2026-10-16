# LiveSync uninstaller. Run Uninstall.bat (double-click) rather than this file directly.
# Removes the plugins (all years); leaves your settings and logs in %APPDATA%\C3dRevitLiveSync.

$ErrorActionPreference = 'Stop'

if (Get-Process -Name acad, Revit -ErrorAction SilentlyContinue) {
    Write-Host 'Please close Civil 3D and Revit first, then run Uninstall.bat again.' -ForegroundColor Yellow
    exit 1
}

$paths = @(Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\LiveSync.bundle')
foreach ($year in 2025, 2026, 2027) {
    $paths += Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\LiveSync"
    $paths += Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\LiveSync.Revit.addin"
}
foreach ($p in $paths) {
    if (Test-Path $p) { Remove-Item $p -Recurse -Force; Write-Host "Removed $p" -ForegroundColor Green }
}
Write-Host ''
Write-Host "LiveSync is uninstalled. Your settings and logs are still in $env:APPDATA\C3dRevitLiveSync (delete that folder too if you want)." -ForegroundColor Cyan
