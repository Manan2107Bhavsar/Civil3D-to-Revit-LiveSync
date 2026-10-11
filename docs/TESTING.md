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

## Results

| Date | Test | Result | Notes |
|---|---|---|---|
| 2026-10-10 | 0.1 | ✅ 25/25 | dev machine |
| | 0.2 | ⏳ | waiting for you to import in Civil 3D |
