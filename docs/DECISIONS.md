# Decisions

| Date | Decision | Alternatives | Why |
|---|---|---|---|
| 2026-10-10 | Target Civil 3D 2027 + Revit 2027, `net10.0-windows` | Civil 3D 2026 | Only 2027 is installed; both hosts run .NET 10. |
| 2026-10-10 | Install `mcp-servers-for-revit` (monorepo) patched for R27, not archived `revit-mcp` | archived repo | `revit-mcp` is server-only and deprecated; the monorepo includes the Revit add-in. |
| 2026-10-10 | Own named pipe `\.\pipe\c3d-revit-livesync`, length-prefixed JSON | reuse port 8080 | 8080 has no framing (8 KB single read), binds all interfaces, and blocks per command. A pipe is local-only and duplex. |
| 2026-10-10 | Separate Revit receiver add-in | extend MCP add-in | Keeps MCP untouched; the sync loop doesn't depend on MCP. |
| 2026-10-10 | Detect via `Database.ObjectModified` + UI-thread timer debounce (750 ms, idle, no command active) | `DBObject.Modified`, `Application.Idle`, thread-pool timer | Survives reopen; Idle stops firing when AutoCAD is fully idle; host objects must be read on the main thread. |
| 2026-10-10 | Update toposolid in place: `ResetSlabShape` → `Enable` → `AddPoints` | delete + recreate | Keeps ElementId, type, material, hosted elements; one undo step. |
| 2026-10-10 | Boundary change → rewrite sketch via `SketchEditScope` | warn + clip; recreate | User choice; keeps ElementId. Fallback: warn + clip. |
| 2026-10-10 | Densify along Civil breaklines | points only; DirectShape | Revit re-triangulates; densified breaklines approximate ridges/ditches cheaply. |
| 2026-10-10 | Keep repo in OneDrive; build output redirected to `%LOCALAPPDATA%` | move repo | User choice; redirect avoids sync of locked DLLs. |
| 2026-10-11 | Revit add-in references the installed `C:\Program Files\Autodesk\Revit 2027\RevitAPI(UI).dll` (`Private=false`) | `Nice3point.Revit.Api.*` pinned to `2027.2.*` | Compiles against exactly the runtime API (27.2); no NuGet version drift; no `Nice3point.Revit.Build.Tasks` auto-publish into `Addins\2027`. Same pattern as the Civil project. |
| 2026-10-11 | Revit deploy: each build to `deploy\revit\<stamp>\`, build rewrites `Addins\2027\LiveSync.Revit.addin` to point there | copy into `Addins\2027\LiveSync\`; ask user to close Revit | Revit locks loaded DLLs; this way builds never fail while Revit is open, and a restart picks up the newest. |
| 2026-10-11 | `PipeServer` and `LatestWinsMailbox` live in `LiveSync.Shared` | inside the Revit add-in | BCL-only (`System.IO.Pipes`), so they're unit-tested over a real pipe without Revit; the step 5 Civil client sits beside them. |
| 2026-10-11 | Pipe: `CurrentUserOnly`, multi-client, `FirstPipeInstance` on the first instance | single client; no ACL | Only this Windows user can connect; later Inventor/SolidWorks clients can share it; a second Revit reports `busy` instead of silently splitting traffic. |
| 2026-10-11 | Step 3 ack status `received`; mailbox drop → `stale` | reuse `skipped-same-hash` | Honest statuses; additive constants, so no schema bump. From step 4 the ack is sent after the transaction commits (`applied`). |
