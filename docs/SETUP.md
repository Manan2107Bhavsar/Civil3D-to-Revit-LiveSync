# Setup (from a clean machine)

Grows with each build step. Steps marked *(later)* aren't written yet.

## 1. Prerequisites

| Need | Version | Check |
|---|---|---|
| Civil 3D | 2027 | `C:\Program Files\Autodesk\AutoCAD 2027\C3D\AeccDbMgd.dll` exists |
| Revit | 2027 | `C:\Program Files\Autodesk\Revit 2027\RevitAPI.dll` exists |
| .NET SDK | 10.x | `dotnet --list-sdks` |
| Python | 3.10+ | only for `tools/` (test data) |
| Git | any | |

## 2. Build and test the shared library

```powershell
cd "<repo>"
dotnet test C3dRevitLiveSync.sln
```

Expected: `Passed! - Failed: 0`. Output goes to `%LOCALAPPDATA%\C3dRevitLiveSync\build\` (set in `Directory.Build.props`). It stays out of the repo so OneDrive doesn't sync DLLs that Civil 3D or Revit have locked.

## 3. Config file

Copy `config/mapping.example.json` to `%APPDATA%\C3dRevitLiveSync\mapping.json`. The plugins fill in the surface handle and toposolid id when you link them, so for now only `transform.units` matters (`m` for metric drawings).

Set the environment variable `LIVESYNC_HOME` to use a different folder (for example a second, isolated test setup).

## 4. Test drawing (Civil 3D)

1. Run `python tools/make_test_surface.py`. This writes `testdata/small-site.xml` and `testdata/breaklines.scr`.
2. In Civil 3D 2027, start a new drawing from the metric template `_Autodesk Civil 3D (Metric) NCS.dwt`.
3. Run `LANDXMLIN` (Insert tab → LandXML) and pick `testdata/small-site.xml`. Import the surface **EG**.
4. Check: Prospector → Surfaces → EG → *Surface Properties* → *Statistics* shows **306 points** and **544 triangles**, with elevations 500.047–502.992 m.
5. Check: EG → Definition → **Breaklines** lists *Ditch* and *Ridge*. If it's empty, run `SCRIPT` → `testdata/breaklines.scr`. That draws two 3D polylines; add them to EG as standard breaklines (Definition → Breaklines → Add).
6. Save as `C:\Projects\LiveSyncTest\Site.dwg`, or update `civil.dwgPath` in the mapping to wherever you saved it.

## 5. Civil 3D plugin *(later, step 1)*

## 6. Revit receiver add-in *(later, step 3)*

## 7. Revit test project and shared coordinates *(later, step 4/6)*

## Revit MCP (separate, already installed)

`mcp-servers-for-revit` was built for Revit 2027 from `C:\Users\bhavs\source\repos\mcp-servers-for-revit` (branch `revit2027`) and installed to `%APPDATA%\Autodesk\Revit\Addins\2027\`. LiveSync doesn't need it. To rebuild it:

```powershell
cd C:\Users\bhavs\source\repos\mcp-servers-for-revit
dotnet build plugin\RevitMCPPlugin.csproj -c "Release R27"
dotnet build commandset\RevitMCPCommandSet.csproj -c "Release R27"
Copy-Item "plugin\bin\AddIn 2027 Release R27\*" "$env:APPDATA\Autodesk\Revit\Addins\2027\" -Recurse -Force
```
