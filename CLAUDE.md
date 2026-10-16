# CLAUDE.md — Civil 3D → Revit Live Surface Sync

Project notes for future Claude Code sessions. **Start with `docs/HANDOFF.md`** (purpose, locations, status, next action), then `docs/PLAN.md`. Record decisions in `docs/DECISIONS.md` and keep HANDOFF's status/next-action current at the end of each session.

## Environment (verified 2026-10-10)
- Civil 3D **2027** only (`C:\Program Files\Autodesk\AutoCAD 2027\C3D\AeccDbMgd.dll`). AutoCAD 2025/2026 have no C3D, despite older notes saying "Civil 3D 2026".
- Revit **2027** (27.2) only. Toposolid API is available; there's no need for the TopographySurface fallback.
- Both hosts run **.NET 10**, so target `net10.0-windows`. The SDK is 10.0.303.
- **Multi-year (2026-10-16):** the plugins also build for 2025/2026 (.NET 8) via `-p:HostYear=2025|2026|2027` (default 2027, set in `Directory.Build.props`; bin/obj get a `<year>\` subfolder). 2025/2026 reference NuGet: `AutoCAD.NET` 25.0.1 / 25.1.0 (25.0.0 has a broken dependency), `Civil3D.NET` 13.7.145 / 13.8.280 (Autodesk, includes AecBaseMgd), `Nice3point.Revit.Api.*` 2025.0.2 / 2026.0.4, all `ExcludeAssets="runtime"`. Defines `CIVIL<year>` / `REVIT<year>`. Shared targets `net8.0;net10.0`, so guard .NET 9+ APIs with `#if NET9_0_OR_GREATER` (e.g. `Convert.ToHexStringLower`, `AllowOutOfOrderMetadataProperties`). On .NET 8, `"type"` must be first in a message. 2025/2026 aren't installed here, so they're compile-checked only.
- Revit MCP: `C:\Users\bhavs\source\repos\mcp-servers-for-revit`, local branch `revit2027`. Don't change its behaviour without asking the user.
- Civil 3D MCP (the user's own, Python + pywin32): `..\Jarvis\civil3d_bridge.py` (the logic), wrapped by `..\Jarvis\civil3d_mcp_server.py` (FastMCP) and `..\Jarvis\jarvis\mcp\servers\cad\server.py` (Jarvis). It isn't part of the sync loop; it's a candidate for Phase 2+ AI tools. Civil 3D 2027's COM ProgID is `AeccXUiLand.AeccApplication.13.9` (the only one registered). AutoCAD 2027 is `AutoCAD.Application.26`.

## Build / test
- `dotnet test C3dRevitLiveSync.sln` (all shared tests, on net8.0 and net10.0). `tools/package-release.ps1` builds the release zip for all years (`-p:SkipDeploy=true`). Build output goes to `%LOCALAPPDATA%\C3dRevitLiveSync\build\{bin,obj}\<Project>\` via `Directory.Build.props`; never commit or expect bin/obj in the repo.
- `python tools/make_test_surface.py` regenerates `testdata/`.
- Runtime files live in `%APPDATA%\C3dRevitLiveSync\` (`mapping.json`, `logs/`); `LIVESYNC_HOME` overrides this.
- `LiveSync.Shared` must stay BCL-only (System.Text.Json): Revit and AutoCAD each load their own Newtonsoft, and a shared dependency invites version clashes.

## Conventions
- Message shapes live only in `src/Shared/Messages.cs`; add a kind with `[JsonDerivedType]` plus a `MessageTypes` const. Bump `MessageCodec.SchemaVersion` only for breaking changes.
- Payload coordinates are raw Civil world coordinates in drawing units; the unit conversion and shared→internal transform happen on the Revit side only.
- Log through `JsonlLog` with a `stage` name and `mappingId`/`version` fields so the two sides' logs can be joined.

## API lookup (don't guess)
- Revit: the installed Revit ships its own XML docs: `C:\Program Files\Autodesk\Revit 2027\RevitAPI.xml` / `RevitAPIUI.xml` (these match the 27.2 runtime; the NuGet copy is 2027.3). Grep `member name="M:Autodesk.Revit.DB.<Type>.<Member>`.
- Civil 3D / AutoCAD: no XML docs are installed. Use the reflection probe in the session scratchpad (`apiprobe`, MetadataLoadContext) or rebuild it: load `AeccDbMgd.dll`/`acdbmgd.dll`/`accoremgd.dll` from `C:\Program Files\Autodesk\AutoCAD 2027[\C3D]`.

## Gotchas learned
- Revit 2027 **removed** `Curve.Intersect(Curve, out IntersectionResultArray)`. Use `Intersect(Curve, CurveIntersectResultOption)` → `CurveIntersectResult.GetOverlaps()`.
- `Nice3point.Revit.Build.Tasks` with `PublishAddinFiles=true` auto-copies build output into `%APPDATA%\Autodesk\Revit\Addins\<ver>\<ProjectName>\`, even for Release builds.
- `Nice3point.Revit.Api.*` `2027.*` resolves to the newest API (2027.3) while the installed Revit is 27.2. Our add-in therefore references the installed `RevitAPI.dll`/`RevitAPIUI.dll` by HintPath (`Private=false`) instead of NuGet.
- The Revit add-in builds to a new `%LOCALAPPDATA%\C3dRevitLiveSync\deploy\revit\<stamp>\` folder, and the build rewrites `%APPDATA%\Autodesk\Revit\Addins\2027\LiveSync.Revit.addin`. Building is fine while Revit runs, but restart Revit to load the new build.
- In the Revit project, `Units` is ambiguous with `Autodesk.Revit.DB.Units`. Use the `using Units = LiveSync.Shared.Units;` alias.
- The 2027 docs don't define the Z semantics of `SlabShapeEditor.AddPoints` or the `ModifySubElement(vertex, offset)` reference, nor the direction of `ProjectLocation.GetTotalTransform`. The code measures them (it logs `addZDeltaMaxMm`, `cornerZDeltaMaxMm` and `frameInverted`). Check the step 4 results before changing that code.
- After `ResetSlabShape()` + `Enable()`, **call `doc.Regenerate()`** before reading `SlabShapeVertices`, or it comes back empty (verified in step 4). The vertices are then just the sketch corners at the flat top. `AddPoints` can't place points on the edge, so corner heights are set with `ModifySubElement(v, targetZ - flatZ)`; that offset is relative to the flat top. `AddPoints` Z is absolute. `ProjectLocation.GetTotalTransform()` is shared→internal (all verified 2026-10-11).
- Toposolid `AddPoints` throws `InvalidOperationException: Could not add point … at index N` (undocumented) for a point **below the flat top − thickness**. Points above the flat top are fine. `Toposolid.Create` puts the flat top at the lowest point. Fix (verified 2026-10-12, TESTING 5.13): AddPoints in a SubTransaction; on failure, lower `TOPOSOLID_HEIGHTABOVELEVEL_PARAM` so the flat top equals the lowest target, then retry. The base is never raised again afterwards.
- Civil plugin: `Message` is ambiguous with `System.Windows.Forms.Message`; alias `using Message = LiveSync.Shared.Message;`.
- Bash heredocs containing `'''` or other odd quoting can fail to parse. For multi-line Python patches, write the script with the Write tool and run it from the scratchpad.
- `PipeServer` callbacks run on pool threads. Never touch the Revit API there; only post to the mailbox and raise the ExternalEvent.
- In 2027, `IExternalCommandAvailability.IsCommandAvailable` has extra overloads (UIControlledApplication, VSTA). Implementing the `UIApplication` one compiles. Without an availability class, external commands are disabled when no project is open.
- The MCP add-in socket (8080) has no framing (single 8 KB read = one message) and binds `IPAddress.Any`, so don't send large payloads over it. LiveSync uses its own named pipe.
- Toposolid `SlabShapeEditor.AddPoints` requires points to be inside the sketch boundary and distinct in XY. `ResetSlabShape()` disables the editor, so call `Enable()` again afterwards.
- From earlier COM work: COM/host objects can't cross threads, and direct COM ModelSpace drawing was unreliable in C3D. This project uses in-process .NET only and reads host objects only on the main thread.
- LandXML point order is **Northing Easting Elevation** (`<P>N E Z</P>`), not X Y Z.
- In this environment, Bash heredocs collapse `\\` to `\`, even with a quoted `'EOF'`. This broke a JSON file, and Python-in-heredoc edits turned `"\\n"` into real newlines in C#. Write anything containing backslashes with the Write/Edit tools, never via heredocs.
- User decision (step 1): **sync only after the command ends**. A half-finished command (e.g. mid Add Point) would show a broken-looking mesh in Revit. Don't add mid-command syncing.
- Civil TIN extraction: use `GetTriangles(includeInvisible:false)` and build vertices from those triangles. `TinSurfaceVertex` has no index, so dedupe by exact XY. Densified breakline z comes from `FindElevationAtXY` (it throws outside the surface).
- Civil 3D's managed base types are in `C:\Program Files\Autodesk\AutoCAD 2027\ACA\AecBaseMgd.dll` (not `C3D\`). Reference it with `Private=false` along with acdbmgd/acmgd/accoremgd/AeccDbMgd.
- With `UseWindowsForms`, `Application` and `Timer` are ambiguous. Use the `AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application` alias and `System.Windows.Forms.Timer` fully qualified. The same goes for `Entity` (alias `TinSurface` from `Autodesk.Civil.DatabaseServices`).
- The Civil plugin builds to a new `%LOCALAPPDATA%\C3dRevitLiveSync\deploy\civil3d\<stamp>\` folder each time, plus `livesync-load.lsp`. The user must restart Civil 3D to load a new build.
- A NETLOADed assembly can't be unloaded. Restart Civil 3D to load a rebuilt DLL, or build to a versioned folder.
