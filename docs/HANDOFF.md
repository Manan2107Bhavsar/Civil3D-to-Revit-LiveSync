# Session handoff — Civil 3D → Revit Live Surface Sync

Last updated: **2026-10-11**, session 2 (step 2 verified, step 3 built). A new session should read this first, then `CLAUDE.md`, then `docs/PLAN.md`.

**Resume prompt for a new session:**
> Read `docs/HANDOFF.md`, `CLAUDE.md` and `docs/PLAN.md` in `C:\Users\bhavs\OneDrive - Saskatchewan Polytechnic\Civil3D_to_Revit`. Continue from "Next action".

---

## 1. Purpose

Build a desktop tool that keeps a **Civil 3D TIN surface** and a **Revit toposolid** in sync while both programs are open on the same Windows PC. When the surface is edited in Civil 3D, the matching toposolid in Revit updates **in place** within ~1–2 s: same ElementId, type, material and hosted elements. There is no export and no "reload link", and **no AI in the real-time loop** (AI/MCP is only for later features such as change summaries).

- **Phase 1 (current):** one TIN surface → one toposolid, one direction, debounced auto-update, logs with latency timestamps on both sides, optional decimation (off by default), and a force-sync command on both sides.
- **Later phases (design for them, don't build them):** corridor surfaces/solids; SolidWorks/Inventor → Revit family updates with a "this family changed — see difference" notification.
- **Original brief:** `civil3d-revit-livesync-prompt.md` (repo root).
- **Working rules from the user:** plan before coding; small testable steps, each ending with exact manual test steps (drawing, command, expected result); verify Autodesk APIs against docs and never guess; don't modify the user's Revit MCP behaviour without asking; commit to git after each working step; keep `CLAUDE.md` updated with gotchas.

## 2. User

An Engineering Design & Drafting Technology student (Saskatchewan Polytechnic) with civil drafting experience. Expert user of Civil 3D and Revit; has built automation before (Python + pywin32 COM MCP for Civil 3D, NETLOAD .NET DLLs). Works in **metric (m)**. Wants non-obvious API decisions explained.

## 3. Locations

| What | Where |
|---|---|
| **This repo** (git, local only, no remote) | `C:\Users\bhavs\OneDrive - Saskatchewan Polytechnic\Civil3D_to_Revit` |
| Build output (kept out of OneDrive via `Directory.Build.props`) | `%LOCALAPPDATA%\C3dRevitLiveSync\build\{bin,obj}\<Project>\` |
| Civil plugin deploys (a new folder per build) + loader | `%LOCALAPPDATA%\C3dRevitLiveSync\deploy\civil3d\<yyyyMMdd-HHmmss>\`, `...\deploy\civil3d\livesync-load.lsp` |
| Revit add-in deploys (a new folder per build) + manifest | `%LOCALAPPDATA%\C3dRevitLiveSync\deploy\revit\<yyyyMMdd-HHmmss>\`, `%APPDATA%\Autodesk\Revit\Addins\2027\LiveSync.Revit.addin` (rewritten by every build) |
| Test sender | `dotnet run --project tools\LiveSync.SendTool -- [file] [--repeat N] [--bump] [--synthetic N] [--ping]` |
| Runtime config / logs / payloads | `%APPDATA%\C3dRevitLiveSync\mapping.json`, `...\logs\civil3d-<UTC yyyyMMdd>.jsonl`, `...\payloads\<id>.json` (override with env `LIVESYNC_HOME`) |
| Test data | `testdata\small-site.xml` (LandXML), `testdata\breaklines.scr`; user drawing `testdata\site.dwg` (gitignored, already edited during step 1 tests) |
| Revit MCP (installed, separate project) | `C:\Users\bhavs\source\repos\mcp-servers-for-revit`, local branch `revit2027` (commit `cd6c423`, not pushed). Add-in in `%APPDATA%\Autodesk\Revit\Addins\2027\`. Node server registered in Claude Code (user scope) as `mcp-server-for-revit` |
| Archived original Revit MCP (server only, not used) | `C:\Users\bhavs\source\repos\revit-mcp` |
| User's Civil 3D MCP (Python/pywin32) | `C:\Users\bhavs\OneDrive - Saskatchewan Polytechnic\Jarvis\civil3d_bridge.py` (logic), `...\Jarvis\civil3d_mcp_server.py` (FastMCP), `...\Jarvis\jarvis\mcp\servers\cad\server.py` (Jarvis) |
| API reflection probe (session scratch, may be gone) | `%LOCALAPPDATA%\Temp\claude\...\scratchpad\apiprobe` — rebuild per `CLAUDE.md` if needed |

## 4. Environment (verified)

- **Civil 3D 2027** only (C3D 13.9, `C:\Program Files\Autodesk\AutoCAD 2027\C3D\`). The brief said 2026, but AutoCAD 2025/2026 have no C3D. COM ProgID `AeccXUiLand.AeccApplication.13.9`.
- **Revit 2027** (27.2) only. The Toposolid API is available.
- Both hosts run **.NET 10**, so the plugins target `net10.0-windows`. SDK 10.0.303. Python 3.14. Node 24.
- Inventor 2025–2027 is installed (relevant to a later phase).

## 5. Decisions (details and reasons in `docs/DECISIONS.md` / `docs/PLAN.md`)

1. **Civil side:** C# NETLOAD plugin. Detection uses `Database.ObjectModified` filtered by the linked surface's ObjectId. Debounce is 750 ms on a **main-thread WinForms timer**, and it **waits until no command is running**. The user explicitly chose not to sync mid-command, because a half-finished edit would show a broken mesh in Revit.
2. **Transport:** its own **named pipe** `\\.\pipe\c3d-revit-livesync`, with 4-byte LE length-prefixed UTF-8 JSON, duplex (Revit acks and resync requests). **Not port 8080**: the MCP socket has no framing (single 8 KB read), binds `IPAddress.Any` (LAN-exposed, and `send_code_to_revit` runs arbitrary C#), and blocks per command. The user hasn't been asked to change 8080; it's only noted.
3. **Revit side:** a **separate small add-in** (not inside the MCP add-in). Pipe server on a background thread → latest-wins mailbox → `ExternalEvent` → one Transaction: `SlabShapeEditor.ResetSlabShape()` → `Enable()` → `AddPoints()`. Keeps the ElementId. A failures preprocessor suppresses warnings.
4. **Boundary changes:** the user chose to **rewrite the toposolid sketch in place** via `SketchEditScope` (it must start with no transaction open, so it runs as its own phase before the point transaction). Fallback: warn and drop points outside.
5. **Exactness:** Revit re-triangulates the points itself, so breaklines are honoured by **adding extra points along them** (`breaklineDensify`, default 1.0 drawing units, z from `FindElevationAtXY`).
6. **Coordinates:** the payload carries raw Civil world coordinates in drawing units. Revit converts units and applies `inverse(ActiveProjectLocation.GetTotalTransform())`, assuming shared coordinates were acquired from the DWG. The mapping can add an offset or rotation.
7. **Identity:** the surface is keyed by **handle** (name as fallback); the toposolid by **UniqueId** (ElementId as fallback). The mapping file is shared by both apps and written under a cross-process lock.
8. **Hash:** order-independent SHA-256 over coordinates quantized to 0.1 mm, plus triangles, extra points and units. Both sides skip unchanged surfaces.
9. **Repo** stays in OneDrive (user's choice); build output is redirected. Git is local only. Test data is generated.

## 6. Status

| Step | Content | Status |
|---|---|---|
| Plan | `docs/PLAN.md`, `DECISIONS.md`, `CLAUDE.md`, questions answered | ✅ |
| MCP | Revit MCP built for 2027 (added R27 config; fixed `Curve.Intersect` removed in 2027), installed, user confirmed it loads in Revit | ✅ |
| User's Civil MCP | Added ProgID `13.9` to `CIVIL3D_PROGIDS` in `civil3d_bridge.py` (not a git repo; untested against Civil 3D) | ✅ edited |
| 0 | `src/Shared` (LiveSync.Shared): messages, framing, hash, config store, JSONL log, units; xUnit tests; test surface generator; docs | ✅ user verified (EG: 306 pts / 544 tri, breaklines Ditch + Ridge) |
| 1 | `src/Civil3dPlugin`: `LSLINK`, `LSUNLINK`, `LSSTATUS`, `LSRELOAD`, `LSDEBUG`; debounced detection | ✅ user tested 1.1–1.10: one change line per command, ~750–850 ms quiet, waits for command end. 1.11 (close/reopen) and 1.12 (LSDEBUG classes) were **not** done |
| 2 | Extraction (visible triangles, breakline densify, boundary loop), background payload build, hash, `LSSYNC`, payload → `payloads\<id>.json` | ✅ user tested 2026-10-11 (verified from logs): 306/544 + 132 breakline pts, boundary 66; undo and raise/lower restore the exact original hash; warm read ~1.7 ms + prep ~0.5 ms. 1.11/1.12 also done. Only 2.7 (hide boundary) not tried |
| 3 | Revit receiver add-in (`src/RevitReceiver`), `PipeServer` + `LatestWinsMailbox` in Shared, `tools/LiveSync.SendTool` | 🧪 **built + dev-tested without Revit** (real Receiver in a console host, see TESTING results). **Not yet tested inside Revit**: user runs TESTING step 3 (3.1–3.10) |
| 4 | ExternalEvent + in-place toposolid update from a canned payload | ⏭ next to build |
| 5 | Civil pipe client + acks + end-to-end latency logging | planned |
| 6 | Coordinates/units, mapping on the Revit side, sketch rewrite (`SketchEditScope`) | planned |
| 7 | Decimation, force-sync buttons, reconnect robustness, SETUP from a clean machine, large-surface timings | planned |

Git log (repo): `ec88a44` plan → `4298a55` MCP note → `df55844` step 0 → `9b2c799` step 1 → `9c46942` step 1 results + hardening → `2f3440f` step 2 → `5b76213` handoff → step 3 commit (2026-10-11). Tests: `dotnet test C3dRevitLiveSync.sln` → 38 passing.

## 7. Next action

1. **Get the step 3 results from the user** (`docs/TESTING.md` step 3, 3.1–3.10). Read `%APPDATA%\C3dRevitLiveSync\logs\revit-*.jsonl` and record the results in TESTING. Things to watch: whether Revit shows the "unsigned add-in" prompt; the `pipe.busy` path with two Revits; whether a 6 MB payload freezes the UI (it shouldn't: the pipe and decode run on pool threads).
2. **Build step 4:** in `Receiver`, create an `ExternalEvent` (in `OnStartup`, valid API context) and `Raise()` it after each mailbox post. Its handler `TryTake`s each mapping and:
   - finds the doc and toposolid. Until the step 6 Link button exists, use a temporary rule: the selected toposolid, or the only toposolid in the active doc;
   - converts m → internal ft with **no shared transform yet** (step 6). For step 4 the test project's toposolid must sit near Civil coordinates, or subtract a temporary offset;
   - runs one `Transaction`: `GetSlabShapeEditor()` → `ResetSlabShape()` → `Enable()` → `AddPoints()`, with an `IFailuresPreprocessor`;
   - acks `applied` (pointsApplied/Dropped, eventStartUtc, committedUtc) after the commit.

   Verify every member in `C:\Program Files\Autodesk\Revit 2027\RevitAPI.xml` first. Remember: points must be inside the sketch and distinct in XY.
3. Then steps 5–7 per `docs/PLAN.md` §4. In step 5 the Civil pipe client (`PipeClient`, Shared, reconnect every 2 s, `CurrentUserOnly`) goes next to `PipeServer`.

## 8. Open / watch items

- Whether Civil's `GetTinProperties().NumberOfTriangles` counts hidden triangles (TESTING 2.7 will show this; it only affects display).
- Whether LandXML-imported breaklines come back through `SurfaceBreakline.Vertices` (expect 2 breaklines and 132 extra points).
- `GetTotalTransform()` on `ProjectLocation` (inherited from `Instance`): confirm in step 6.
- Toposolid `AddPoints` performance at 10k–50k points is unknown; measure in step 7.
- Port 8080 binds all interfaces. This was mentioned to the user but not changed (their MCP; ask before touching).
- Old deploy folders under `deploy\civil3d\` accumulate; deleting them once Civil 3D is closed is safe.
