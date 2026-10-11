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
