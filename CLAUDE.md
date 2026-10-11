# CLAUDE.md — Civil 3D → Revit Live Surface Sync

Project notes for future Claude Code sessions. Read `docs/PLAN.md` first; record decisions in `docs/DECISIONS.md`.

## Environment (verified 2026-10-10)
- Civil 3D **2027** only (`C:\Program Files\Autodesk\AutoCAD 2027\C3D\AeccDbMgd.dll`). AutoCAD 2025/2026 have no C3D, despite older notes saying "Civil 3D 2026".
- Revit **2027** (27.2) only. Toposolid API is available; there's no need for the TopographySurface fallback.
- Both hosts run **.NET 10**, so target `net10.0-windows`. The SDK is 10.0.303.
- Revit MCP: `C:\Users\bhavs\source\repos\mcp-servers-for-revit`, local branch `revit2027`. Don't change its behaviour without asking the user.
- Civil 3D MCP (the user's own, Python + pywin32): `..\Jarvis\civil3d_bridge.py` (the logic), wrapped by `..\Jarvis\civil3d_mcp_server.py` (FastMCP) and `..\Jarvis\jarvis\mcp\servers\cad\server.py` (Jarvis). It isn't part of the sync loop; it's a candidate for Phase 2+ AI tools. Civil 3D 2027's COM ProgID is `AeccXUiLand.AeccApplication.13.9` (the only one registered). AutoCAD 2027 is `AutoCAD.Application.26`.

## API lookup (don't guess)
- Revit: the XML docs ship in the NuGet package: `~/.nuget/packages/nice3point.revit.api.revitapi/2027.*/ref/net10.0-windows7.0/RevitAPI.xml`. Grep `member name="M:Autodesk.Revit.DB.<Type>.<Member>`.
- Civil 3D / AutoCAD: no XML docs are installed. Use the reflection probe in the session scratchpad (`apiprobe`, MetadataLoadContext) or rebuild it: load `AeccDbMgd.dll`/`acdbmgd.dll`/`accoremgd.dll` from `C:\Program Files\Autodesk\AutoCAD 2027[\C3D]`.

## Gotchas learned
- Revit 2027 **removed** `Curve.Intersect(Curve, out IntersectionResultArray)`. Use `Intersect(Curve, CurveIntersectResultOption)` → `CurveIntersectResult.GetOverlaps()`.
- `Nice3point.Revit.Build.Tasks` with `PublishAddinFiles=true` auto-copies build output into `%APPDATA%\Autodesk\Revit\Addins\<ver>\<ProjectName>\`, even for Release builds.
- `Nice3point.Revit.Api.*` `2027.*` resolves to the newest API (2027.3) while the installed Revit is 27.2. Pin to `2027.2.*` in our projects, so we don't compile against members the runtime lacks.
- The MCP add-in socket (8080) has no framing (single 8 KB read = one message) and binds `IPAddress.Any`, so don't send large payloads over it. LiveSync uses its own named pipe.
- Toposolid `SlabShapeEditor.AddPoints` requires points to be inside the sketch boundary and distinct in XY. `ResetSlabShape()` disables the editor, so call `Enable()` again afterwards.
- From earlier COM work: COM/host objects can't cross threads, and direct COM ModelSpace drawing was unreliable in C3D. This project uses in-process .NET only and reads host objects only on the main thread.
- A NETLOADed assembly can't be unloaded. Restart Civil 3D to load a rebuilt DLL, or build to a versioned folder.
