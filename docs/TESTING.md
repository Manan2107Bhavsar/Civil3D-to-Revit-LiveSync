# Manual test procedures

Each build step adds a section. Record results (date, pass/fail, timings) under **Results**.

## Step 0 — shared library and test data

### 0.1 Unit tests
```powershell
dotnet test C3dRevitLiveSync.sln
```
**Expect:** `Passed! - Failed: 0, Passed: 25`.

These tests cover:
- **Framing:** frames survive being delivered one byte at a time. Truncated and oversized frames are rejected.
- **Messages:** all fields round-trip, including doubles at UTM magnitudes. An unknown `type` or a newer `schema` is rejected.
- **Hash:** the same surface gives the same hash regardless of vertex and triangle order or float noise below 0.1 mm. The hash changes on a 10 mm elevation edit, a swapped edge, a reversed triangle winding, extra points or a unit change.
- **Config:** Civil and Revit each write their own half of `mapping.json` without losing the other's. 20 concurrent writers lose nothing.
- **Log:** each JSONL line parses and has a UTC `t`.

### 0.2 Test surface import
Follow `SETUP.md` §4.
**Expect:** EG shows 306 points and 544 triangles, elevations 500.047–502.992 m, with breaklines Ditch and Ridge in its definition. In 3D orbit you can see the ditch running east–west at N 5776040 and the ridge running north–south at E 383025.

## Step 1 — Civil 3D change detection (nothing is sent yet)

Setup: `Site.dwg` with surface EG, plugin loaded (`SETUP.md` §5). The log is `%APPDATA%\C3dRevitLiveSync\logs\civil3d-<UTC date>.jsonl`.

| # | Do | Expect |
|---|---|---|
| 1.1 | APPLOAD `livesync-load.lsp` | `LiveSync loaded (...\deploy\civil3d\<stamp>\LiveSync.Civil3d.dll)` |
| 1.2 | `LSLINK` → pick EG → Enter | `Linked surface 'EG' (handle …, units m) as mapping '<id>'`. The default id is `eg-site` if you copied the example config (it already matched EG by name on load), otherwise `eg`. Below, `[eg]` means whichever id you got. `mapping.json` has the dwg path, handle and `"units": "m"` |
| 1.3 | `LSSTATUS` | `[eg] attached to 'EG' in Site.dwg; units m; events 0; changes 0` |
| 1.4 | Modify ribbon → Surface → Edit Surface → **Add Point**, click one point, give an elevation, Enter | **Exactly one** `LiveSync [eg]: change #1 detected (...)` about ¾ s after the command ends |
| 1.5 | **Delete Point**, delete 3–4 points in one command, Enter | One line (change #2). The many events are merged into one |
| 1.6 | **Raise/Lower Surface** +0.5 m | One line |
| 1.7 | **Swap Edge** on one edge | One line |
| 1.8 | Start **Move Point**, pick a point, and *wait* several seconds before placing it | No line while the command is active. One line after it ends. The log has a `debounce.wait` entry naming the command |
| 1.9 | `UNDO` once | One line |
| 1.10 | Draw a LINE or edit any non-surface object | **No** line |
| 1.11 | Close Site.dwg, reopen it | Log: `detach` then `attach`. Repeat 1.4: still detected |
| 1.12 | `LSDEBUG` on → Add Point → `LSDEBUG` off | Log has `event.modified` lines with `cls` names. Tell me which classes fire: this shows how Civil 3D writes surface edits, which matters for corridor surfaces in Phase 2 |

When you're done, tell me and I'll read the log file to check the counts and timings.

## Results

| Date | Test | Result | Notes |
|---|---|---|---|
| 2026-10-10 | 0.1 | ✅ 25/25 | dev machine |
| 2026-10-10 | 0.2 | ✅ | EG imported, 306 pts / 544 tri, breaklines Ditch + Ridge present (user) |
| 2026-10-10 | MCP | ✅ | mcp-servers-for-revit loaded in Revit 2027 (user) |
| 2026-10-10 | 1.1–1.3 | ✅ | loaded; LSLINK → `eg`, handle 71B8, units m; attached |
| 2026-10-10 | 1.4–1.10 | ✅ | 7 edits → exactly 7 `debounced` lines, no double fires. Single edits quiet 751–848 ms. Multi-pick commands waited for command end: AECCADDSURFACELINE (3 events / 7.3 s), AECCADDSURFACEPOINT (7 / 14.1 s), AECCEDITSURFACEPOINT (2 / 16.2 s). UNDO of 7 points = 7 events in 82 ms → 1 line |
| 2026-10-10 | 1.11–1.12 | ⏳ | not in log: no detach/attach; LSDEBUG turned on but no edits logged after it |
