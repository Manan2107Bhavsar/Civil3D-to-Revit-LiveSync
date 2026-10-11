# Civil 3D → Revit Live Surface Sync

Keeps a Revit 2027 **toposolid** in step with a Civil 3D 2027 **TIN surface** while both programs are open. Edit the surface in Civil 3D; about 1–2 s later the same toposolid in Revit is updated in place (same ElementId, type, material and hosted elements). No export, no reload link, and no AI in the loop.

```
Civil 3D (NETLOAD plugin) ──named pipe, length-prefixed JSON──▶ Revit (add-in, ExternalEvent)
```

## Status

| Step | | |
|---|---|---|
| 0 | Skeleton, shared message format, hashing, config, logging, test data | ✅ done |
| 1 | Civil plugin: NETLOAD, `LSLINK`, debounced change detection | ⏳ next |
| 2–7 | Extraction, Revit receiver, in-place update, end-to-end, coordinates, decimation | planned |

Details: [`docs/PLAN.md`](docs/PLAN.md) · changes: [`docs/CHANGELOG.md`](docs/CHANGELOG.md)

## Quick start (developers)

```powershell
dotnet test C3dRevitLiveSync.sln        # shared library tests
python tools/make_test_surface.py       # regenerate testdata/
```

Build output goes to `%LOCALAPPDATA%\C3dRevitLiveSync\build\`, not to this OneDrive folder. Full setup is in [`docs/SETUP.md`](docs/SETUP.md) and test procedures are in [`docs/TESTING.md`](docs/TESTING.md).

## Layout

```
docs/      PLAN, SETUP, TESTING, DECISIONS, CHANGELOG
src/Shared             LiveSync.Shared: messages, framing, hash, config, JSONL log (net10.0, BCL only)
src/Civil3dPlugin      (step 1) NETLOAD plugin
src/RevitReceiver      (step 3) Revit add-in
tests/                 xUnit tests
config/                mapping.example.json
testdata/              generated LandXML test surface + breakline script
tools/                 generators and helpers
```
