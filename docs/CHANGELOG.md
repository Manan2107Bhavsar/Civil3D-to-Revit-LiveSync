# Changelog

## Unreleased

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
