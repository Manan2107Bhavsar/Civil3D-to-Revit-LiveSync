# Changelog

## Unreleased

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
