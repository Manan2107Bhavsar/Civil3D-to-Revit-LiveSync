# Changelog

## Unreleased

### Step 5 results + fixes — 2026-10-11
- Step 5 passed live: edit → Revit commit 1.3–2.7 s, ≈ 0.85 s after the Civil command ends; Revit apply 69–130 ms.
- Fix: deep pit (point 200 m below the rest) made `AddPoints` throw on an older toposolid. AddPoints now runs in a SubTransaction; on failure the flat top (`Height Offset From Level`) is lowered to the lowest target and it retries once. The log has `flatTopFt`, `thicknessFt`, `minTargetFt`, `maxTargetFt`, `baseLoweredFt`, `firstAddError`; the error names the Civil point.
- Fix: a parked payload older than the applied version is no longer applied when a project opens (stale check in the apply handler; a successful apply clears an older parked one).

### Step 5 — 2026-10-11
- Shared: `PipeClient` (background connect loop, retries every 2 s, `CurrentUserOnly`, logs only state changes), `PipeServer.BroadcastAsync`, `SurfaceUpdate.Force` (additive). +3 tests (48), including reconnect after a server restart.
- Civil 3D sends each built payload to Revit. Skips it when Revit has acked that hash or it's in flight. On (re)connect, it sends the newest unacked payload.
- Acks are logged (`editToCommitMs`, `sentToCommitMs`, `revitWaitMs`, `revitApplyMs`, `pipeMs`), and the command line shows `applied in Revit X.XX s after edit`. `LSSTATUS` shows the pipe state and the last reply.
- Force Sync (pulled in from step 7): Revit's button broadcasts `resync.request`, and Civil extracts on its main thread and sends with `force`. Without Civil 3D, Revit re-applies the last payload.
- Revit parks a payload whose toposolid isn't open yet and applies it when a project opens (`DocumentOpened`).
- Fixes from the step 4 test: `Regenerate()` after `Enable()` (corner heights); Link asks before creating a second toposolid; boundary points are counted as edge, not outside; `waitMs` not logged for local re-applies.

### Step 4 — 2026-10-11
- Revit: an `ExternalEvent` (`ApplyHandler`) drains the latest-wins mailbox on the API thread. Per mapping, `ToposolidUpdater.Apply` runs one Transaction `LiveSync: <surface> v<N>`:
  - `ResetSlabShape` → `Enable` → `AddPoints` (only points strictly inside the sketch and distinct in XY);
  - sketch-corner heights are set by `ModifySubElement` from the Civil TIN;
  - a failures preprocessor deletes warnings, and errors roll back without a dialog.
- The ack is sent after the commit: `applied` (points applied/dropped, eventStart/committed timing), `skipped-same-hash`, `stale`, or `error` (unlinked, not found, failed).
- Diagnostics: Revit's stored Z is read back and logged as `addZDeltaMaxMm` / `cornerZDeltaMaxMm`, because the docs don't define the Z semantics.
- `CoordinateFrame`: payload units → ft, then shared → internal. It works out which way `GetTotalTransform` goes from `GetProjectPosition(XYZ.Zero)`.
- **Link Toposolid** works:
  - with one toposolid selected, it links that toposolid and applies the last payload;
  - with nothing selected, it creates a toposolid from the last payload. If the surface is more than 10 km from the origin, it first offers to set shared coordinates (E/N = surface centre, elevation 0).
  - Either way it writes `revit` into `mapping.json` (UniqueId, ElementId, path).
- Undo/Redo in Revit clears the "already applied" memory (`DocumentChanged`).
- Shared: `PlanarRegion`, `TinInterpolator`, `ToposolidPlanner` (+7 tests, 45 total).
- SendTool: `--dz D` (raise and re-version), prints applied counts, 30 s ack timeout.

### Step 3 — 2026-10-11
- Shared: `PipeServer` (named pipe, length-prefixed, multi-client, current-user ACL, `busy` if another process owns the name) and `LatestWinsMailbox<T>`; `AckStatus.Received` and `AckStatus.Stale`. 38 tests (6 new, including a real-pipe round trip).
- `src/RevitReceiver` (`LiveSync.Revit.dll`, Revit 2027): `IExternalApplication` starts the pipe server; `Receiver` logs `received` (counts, bytes, decode/transit ms), posts into the mailbox and acks `received`/`stale`. Ribbon panel LiveSync: Status (works with no project open), Link Toposolid and Force Sync stubs.
- Versioned deploy to `deploy\revit\<stamp>\` plus a generated `Addins\2027\LiveSync.Revit.addin`.
- `tools/LiveSync.SendTool`: replays a payload file (or a synthetic N×N grid) over the pipe, with repeat/bump/ping, and prints acks and round-trip times.

### Step 2 — 2026-10-10
- `SurfaceExtractor` (main thread) reads:
  - visible triangles and the vertices they use;
  - breakline points every `breaklineDensify` along each enabled breakline, with z taken from `FindElevationAtXY`;
  - Civil's own point and triangle statistics, for cross-checking.
- `PayloadBuilder` (background thread, serial per mapping) computes the outer boundary loop, the hash, and a `SurfaceUpdate` written to `%APPDATA%\C3dRevitLiveSync\payloads\<id>.json`.
- Version = max(last + 1, Unix ms), so it keeps increasing across Civil 3D restarts.
- `LSSYNC` forces an extraction (ignores the unchanged hash).
- Shared: `TinGeometry.Boundary` (CCW outers / CW holes), `DensifyXY`, `XyProximityFilter`; 32 tests.

### Step 1 — 2026-10-10
- `src/Civil3dPlugin` (`LiveSync.Civil3d.dll`, NETLOAD, net10.0-windows):
  - `LSLINK`, `LSUNLINK`, `LSSTATUS`, `LSRELOAD`, `LSDEBUG`;
  - detects linked-surface edits via `Database.ObjectModified`;
  - 750 ms debounce on a main-thread WinForms timer that waits for the running command to end;
  - one `debounced` log line plus a command-line message per edit burst.
- Each build is deployed to its own timestamped folder, and a generated `livesync-load.lsp` loads the newest one.

### Step 0 — 2026-10-10
- `LiveSync.Shared` (net10.0, BCL only):
  - message types (`surface.update`, `ack`, `resync.request`, `ping`, `error`) selected by a polymorphic `type` field;
  - 4-byte length-prefixed framing;
  - an order-independent SHA-256 surface hash;
  - `mapping.json` model with a cross-process locked update;
  - a JSONL logger;
  - unit conversion (m / ft / US survey ft).
- 25 xUnit tests.
- `Directory.Build.props` sends bin/obj to `%LOCALAPPDATA%` (repo stays in OneDrive).
- `tools/make_test_surface.py` → `testdata/small-site.xml` (306 pts, 544 faces, metric, 2 breaklines) + `breaklines.scr`.
- `config/mapping.example.json`, README, SETUP, TESTING.

### Planning — 2026-10-10
- `docs/PLAN.md`, `docs/DECISIONS.md`, `CLAUDE.md`.
- The Revit MCP (`mcp-servers-for-revit`) was built for Revit 2027 and installed (outside this repo).
