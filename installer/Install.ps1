# LiveSync installer. Run Install.bat (double-click) rather than this file directly.
# Installs for the current Windows user only; no admin rights needed.
#   Civil 3D 2027: %APPDATA%\Autodesk\ApplicationPlugins\LiveSync.bundle   (loads automatically)
#   Revit 2027:    %APPDATA%\Autodesk\Revit\Addins\2027\LiveSync.Revit.addin + LiveSync\

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }

Say ''
Say '=== LiveSync: Civil 3D -> Revit surface sync - installer ===' 'Cyan'
Say ''

$running = Get-Process -Name acad, Revit -ErrorAction SilentlyContinue
if ($running) {
    Say 'Please close Civil 3D and Revit first, then run Install.bat again.' 'Yellow'
    Say ('Still open: ' + (($running | Select-Object -ExpandProperty ProcessName -Unique) -join ', ')) 'Yellow'
    exit 1
}

$hasCivil = Test-Path 'C:\Program Files\Autodesk\AutoCAD 2027\C3D\AeccDbMgd.dll'
$hasRevit = Test-Path 'C:\Program Files\Autodesk\Revit 2027\RevitAPI.dll'
if (-not $hasCivil -and -not $hasRevit) {
    Say 'Neither Civil 3D 2027 nor Revit 2027 was found in C:\Program Files\Autodesk. Nothing to install.' 'Red'
    exit 1
}

if ($hasCivil) {
    $bundle = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\LiveSync.bundle'
    if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
    New-Item -ItemType Directory -Force (Join-Path $bundle 'Contents') | Out-Null
    Copy-Item (Join-Path $here 'PackageContents.xml') $bundle
    Copy-Item (Join-Path $here 'Civil3D\*') (Join-Path $bundle 'Contents') -Recurse
    Get-ChildItem $bundle -Recurse -File | Unblock-File
    Say "[OK] Civil 3D 2027 plugin installed:  $bundle" 'Green'
} else {
    Say '[--] Civil 3D 2027 not found, skipped.' 'Yellow'
}

if ($hasRevit) {
    $addins = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2027'
    $dir = Join-Path $addins 'LiveSync'
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item (Join-Path $here 'Revit\*') $dir -Recurse
    Get-ChildItem $dir -Recurse -File | Unblock-File
    # The Assembly path is relative to this .addin file.
    $manifest = @'
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>LiveSync Receiver</Name>
    <Assembly>LiveSync\LiveSync.Revit.dll</Assembly>
    <FullClassName>LiveSync.Revit.App</FullClassName>
    <ClientId>6B0E3F51-7C2A-4D8E-9A41-2F5C3D7B9E10</ClientId>
    <VendorId>LiveSync</VendorId>
    <VendorDescription>Civil 3D to Revit live surface sync</VendorDescription>
  </AddIn>
</RevitAddIns>
'@
    Set-Content -Path (Join-Path $addins 'LiveSync.Revit.addin') -Value $manifest -Encoding UTF8
    Say "[OK] Revit 2027 add-in installed:     $dir" 'Green'
} else {
    Say '[--] Revit 2027 not found, skipped.' 'Yellow'
}

Say ''
Say 'Done. Start Revit and Civil 3D. In Revit, look for the LiveSync panel on the Add-Ins tab.' 'Cyan'
Say 'If Revit asks about an unsigned add-in, choose "Always Load".' 'Cyan'
