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

## Step 2 — extraction and payload (still nothing sent to Revit)

Setup: restart Civil 3D (it must load the new build), APPLOAD `livesync-load.lsp`. Your step 1 edits changed EG, so for exact numbers start from a **fresh** surface: new drawing from the metric template → `LANDXMLIN testdata\small-site.xml` → save as `testdata\site2.dwg` → `LSLINK` → pick EG → id `eg`. Re-linking the same id points the mapping at the new drawing.

The payload is written to `%APPDATA%\C3dRevitLiveSync\payloads\<id>.json`.

| # | Do | Expect |
|---|---|---|
| 2.1 | `LSSYNC` | `LiveSync [eg]: 306 pts / 544 tri (Civil: 306 pts / 544 tri), +132 breakline pts from 2 breaklines, boundary 66 pts; read … ms, prep … ms; v… -> …\payloads\eg.json` |
| 2.2 | `LSSYNC` again | Same counts, new `v`, `UNCHANGED (written anyway)` (forced) |
| 2.3 | Add Point inside the surface | After the change line: `307 pts / 546 tri (Civil: 307 / 546)`, a new hash, and the file is rewritten |
| 2.4 | Add Point, then `UNDO` | The undo line shows the original 306 / 544 counts. Its hash equals the one from 2.1 (check `hash` in the log) |
| 2.5 | Raise/Lower the surface +1 m, then −1 m | Counts unchanged and hash changes each time. After −1 m it should equal the original hash (exact if Civil 3D restores elevations exactly) |
| 2.6 | Open `payloads\eg.json` in VS Code | One line of JSON: `"type":"surface.update"`, `"units":"m"`, `vertices` at ~383000 / 5776000 / 500, `boundary` with 132 numbers (66 xy pairs) |
| 2.7 | Add a **hide boundary** inside EG (Definition → Boundaries → Add, Hide type) around a few triangles | Fewer triangles than Civil's total. The line notes `1 holes; only the largest outer is used` |

*Why are the Civil and LiveSync counts compared:* LiveSync counts visible triangles and the vertices they use, while Civil's Statistics count everything. They match on a surface with no hide boundaries, and test 2.7 shows the difference.

## Step 3 — Revit receives payloads over the pipe (nothing applied yet)

Setup: close Revit (or leave it open; see SETUP §6) → `dotnet build src\RevitReceiver\LiveSync.Revit.csproj` → start Revit 2027 → **Always Load** for "LiveSync Receiver". Civil 3D is not needed: the console sender replays the step 2 payload. Open a PowerShell in the repo folder. `SendTool` below means `dotnet run --project tools\LiveSync.SendTool --`.

| # | Do | Expect |
|---|---|---|
| 3.1 | Start Revit, no project open. Add-Ins tab → LiveSync → **Status** | Panel with 3 buttons. Status: `Pipe: \\.\pipe\c3d-revit-livesync [listening]`, clients 0, `No payload received yet.` |
| 3.2 | `SendTool` | `Sent v1791753924947: 17.9 KB …` then `ack v…: received (received and queued (step 3: not applied)) round trip … ms` and `Done: 1/1 replies.` |
| 3.3 | **Status** again | `Last payload: eg v1791753924947`, `307 vertices + 132 breakline points, 17.9 KB`, decode and pipe transit ms |
| 3.4 | `SendTool --repeat 3` | 3 acks `stale (older than a pending version; dropped)`. Nothing consumes the mailbox until step 4, so the same version counts as old |
| 3.5 | `SendTool --repeat 5 --bump` | 5 acks `received`. Status: `superseded` goes up by 5 (each new version replaced the pending one), `pending: 1` |
| 3.6 | Open any project and orbit/pan a 3D view **while** running `SendTool --synthetic 300 --repeat 5 --bump` (90,000 pts, ~6 MB each) | Revit stays responsive the whole time; 5 acks `received` |
| 3.7 | `SendTool --ping` | `pong` |
| 3.8 | Start a **second** Revit 2027 → Status in it | `[busy]` and "Another Revit session owns the pipe". The first Revit still receives (repeat 3.2). Close the second one |
| 3.9 | Close Revit, then `SendTool` | `No server within 5 s. Is Revit running …` (exit code 1) |
| 3.10 | Send me (or open) `%APPDATA%\C3dRevitLiveSync\logs\revit-<today>.jsonl` | Stages: `start`, `pipe.listening`, `pipe.connected`, `received` (vertices, bytes, decodeMs, transitMs), `mailbox.superseded`, `pipe.disconnected`, `stop` |

## Step 4 — Revit updates a toposolid in place (from the SendTool; Civil 3D not needed)

Setup: **restart Revit** (it loads the new build). New project from the **metric** architectural template, then save it as `testdata\site.rvt`. `SendTool` means `dotnet run --project tools\LiveSync.SendTool --` (it sends `payloads\eg.json`: EG with 307 vertices, extent E 383000–383085, N 5776000–5776080).

Reference points (Civil, m) for the spot checks: **NE corner E 383085 N 5776080 → 502.992**; **W edge E 383000 N 5776040 → 500.047**; **interior E 383005 N 5776005 → 500.802**.

| # | Do | Expect |
|---|---|---|
| 4.1 | `SendTool` | ack `error (mapping 'eg' has no linked toposolid; …)`, because nothing is linked yet |
| 4.2 | Select nothing → LiveSync → **Link Toposolid** | Dialog "The surface is far from this project's origin … ~5,788 km" → **Yes**. Then `Toposolid <id> linked to 'eg'`. A toposolid appears near the project origin |
| 4.3 | In a 3D view, place **Spot Elevations** on the toposolid at the 3 reference points (snap to the vertices) | 502.992 / 500.047 / 500.802 (to the mm). If the spot elevation's origin is the Project Base Point, it's the same value here, because elevation was set to 0. **Write down what you get**, especially if it's off |
| 4.4 | Select the toposolid. Note its **ElementId** (Manage → IDs of Selection) | — |
| 4.5 | `SendTool --dz 2` | ack `applied … N pts applied`. The toposolid rises 2 m, with the same shape. The spot elevations update to 504.992 / 502.047 / 502.802 |
| 4.6 | Check the ElementId and the type/material | Same ElementId as 4.4; type unchanged |
| 4.7 | Undo (Ctrl+Z) once | The undo list's top entry is `LiveSync: EG v…`, and one undo puts the toposolid back exactly to 4.3 |
| 4.8 | `SendTool --dz 2` again | applied again (not skipped: Undo resets the "already applied" memory) |
| 4.9 | `SendTool --dz 1 --repeat 5 --interval 0` (5 versions back to back, each 1 m higher) | Some acks `stale (superseded …)`. The **last** one is `applied`, and the toposolid ends at +5 m (507.992 at the NE corner) |
| 4.10 | `SendTool` (original, 4.3 heights) while you're **inside a command** in Revit (e.g. Wall tool active, mid-placement) | No change until you finish/Esc the command. Then it applies by itself. SendTool gives up after 30 s; that's fine, just check that Revit applied it |
| 4.11 | Select the EG toposolid → **Link Toposolid** | `Toposolid <same id> linked to 'eg'. Applying eg v… to it now.` One `applied` line appears in the log, and nothing moves (same surface). This is the link-existing path |
| 4.12 | Hide the EG toposolid. `SendTool --synthetic 150 --bump` (22,500 pts, mapping `synthetic`; ack `error … no linked toposolid` is expected) → select nothing → **Link Toposolid** → `SendTool --synthetic 150 --dz 1` | A 149 × 149 m rolling toposolid is created (overlapping EG's area, hence hiding EG), then rises 1 m. Note the ack round trip: this is the first big-surface timing |
| 4.13 | Send me `logs\revit-<today>.jsonl` (or just tell me "done") | Stages `link`, `created`, `applied` with `addZDeltaMaxMm`, `cornerZDeltaMaxMm`, `applyMs`, `frameInverted` |

*What 4.3/4.5 check:* the API docs don't say whether `AddPoints` Z is absolute, or what the vertex offset in `ModifySubElement` is measured from. The add-in reads back what Revit stored and logs the difference (`addZDeltaMaxMm`, `cornerZDeltaMaxMm`; 0 means correct), and your spot elevations confirm it visually.

## Step 5 — live: edit in Civil 3D → Revit updates by itself

Setup: close Civil 3D and Revit (both load new builds).
1. Start **Revit** and open `testdata\site.rvt` (toposolid 3021 is linked to `eg`).
2. Start **Civil 3D**, open `testdata\site2.dwg` and APPLOAD `%LOCALAPPDATA%\C3dRevitLiveSync\deploy\civil3d\livesync-load.lsp`.

Arrange both windows side by side, with Revit in a 3D view of the toposolid.

| # | Do | Expect |
|---|---|---|
| 5.1 | Watch the Civil command line after APPLOAD; then `LSSTATUS` | Within 2 s: `LiveSync: connected to Revit.` LSSTATUS: `Revit: connected since …` |
| 5.2 | Revit → LiveSync → Status | `Clients connected: 1` |
| 5.3 | Civil: `LSSYNC` | `LiveSync [eg] v…: applied in Revit 0.xx s after send (373 pts; Revit apply … ms).` The toposolid goes back to the real EG (the +2 m from step 4 disappears) |
| 5.4 | Civil: edit one surface point's elevation (+3 m) somewhere visible, or Raise/Lower Surface +1 m | Revit updates **by itself** about 1 s later. Civil prints `applied in Revit 1.xx s after edit`. **Write down the times** for a few edits |
| 5.5 | Civil: Add Points, picking 3 points in one command, then Enter | Nothing in Revit while the command runs; **one** update after Enter |
| 5.6 | Civil: `UNDO` | Revit follows back |
| 5.7 | Revit: start the Wall tool (stay in it). Civil: make an edit | Civil prints only the extraction line. Press Esc in Revit → it applies right away, and Civil prints `applied …` with a bigger time |
| 5.8 | Close Revit (don't save). Civil: make an edit | `Revit disconnected; will reconnect.` then `Revit not connected; v… will be sent when it connects.` |
| 5.9 | Start Revit, open site.rvt | Civil: `connected to Revit` → `Revit error: … not found …; will apply when the project is opened` (Revit is still on the home screen) → after the project opens, the edit from 5.8 appears in Revit, and Civil prints `applied …` |
| 5.10 | Revit → LiveSync → **Force Sync** | Revit: "Asked Civil 3D for a fresh surface". Civil: `Force Sync requested by Revit.` then `applied …` |
| 5.11 | Civil: `LSSTATUS` | `last Revit reply: v… applied, x.xx s after edit` |
| 5.12 | Tell me "done" | I read both logs and record the edit→Revit latency (debounce 750 ms + extraction + pipe + Revit) |
| 5.13 | Re-test of the deep-pit fix. (a) Civil: set the spike points (300 / 400 / 510 / 520 / 600 m) back to ≈ 500 m; wait for `applied`. (b) Revit: delete the linked toposolid → LiveSync → Link Toposolid with nothing selected (creates a fresh one **without** the pit, like 3021 was). (c) Civil: set one point to **300 m** | Revit applies it (no error). Civil: `applied …`. The Revit log line has `baseLoweredFt` ≈ 656 (200 m) if the retry was needed, and `flatTopFt`/`minTargetFt` either way |

## Results

| Date | Test | Result | Notes |
|---|---|---|---|
| 2026-10-10 | 0.1 | ✅ 25/25 | dev machine |
| 2026-10-10 | 0.2 | ✅ | EG imported, 306 pts / 544 tri, breaklines Ditch + Ridge present (user) |
| 2026-10-10 | MCP | ✅ | mcp-servers-for-revit loaded in Revit 2027 (user) |
| 2026-10-10 | 1.1–1.3 | ✅ | loaded; LSLINK → `eg`, handle 71B8, units m; attached |
| 2026-10-10 | 1.4–1.10 | ✅ | 7 edits → exactly 7 `debounced` lines, no double fires. Single edits quiet 751–848 ms. Multi-pick commands waited for command end: AECCADDSURFACELINE (3 events / 7.3 s), AECCADDSURFACEPOINT (7 / 14.1 s), AECCEDITSURFACEPOINT (2 / 16.2 s). UNDO of 7 points = 7 events in 82 ms → 1 line |
| 2026-10-10 | 1.11–1.12 | ⏳ | not in log: no detach/attach; LSDEBUG turned on but no edits logged after it |
| 2026-10-11 | 1.11 | ✅ | close/restart: `detach`/`stop` then `start`/`attach` to the same handle 71B8 in each new session (site.dwg, then site2.dwg after relink) |
| 2026-10-11 | 1.12 | ✅ | LSDEBUG on → one edit logs ~20 `event.modified` lines (AcDbXrecord, AcDbDictionary, AeccDbNetworkCatalogDef, style caches…); only `AeccDbSurfaceTin` 71B8 is `linked:"eg"` — confirms the ObjectId filter is needed |
| 2026-10-11 | 2.1 | ✅ | fresh site2.dwg: 306 pts / 544 tri (Civil 306/544), +132 breakline pts from 2 breaklines, boundary 66 pts, 1 outer / 0 holes. First run read 33–35 ms, prep 15 ms (JIT); later runs read ~1.7 ms, prep ~0.5 ms |
| 2026-10-11 | 2.2 | ✅ | forced LSSYNC with no change → `unchanged:true`, same hash, file written anyway |
| 2026-10-11 | 2.3 | ✅ | AECCADDSURFACEPOINT (2 points, one command) → 307/546, new hash. Also: delete line → 304/536, boundary 74 pts; move point → 305/542 |
| 2026-10-11 | 2.4 | ✅ | undo of delete-line → hash `dceabd…` = the 2.1 hash exactly |
| 2026-10-11 | 2.5 | ✅ | raise → hash `7ff284…` (counts unchanged); lower → back to `dceabd…` exactly |
| 2026-10-11 | 2.6 | ✅ | payload: `surface.update`, units m, first vertex 383005 / 5776005 / 500.802, 66 boundary pts, 17.9 KB |
| 2026-10-11 | 2.x | ✅ | non-geometric surface modifications produce `unchanged:true` extractions (hash skip works) |
| 2026-10-11 | 2.7 | ⏳ | hide boundary not tried (all runs show `holes:0`) |
| 2026-10-11 | 3 (dev) | ✅ | without Revit: the real `Receiver` in a console host + SendTool. eg.json 17.9 KB: round trip 50 ms cold / 1.5–5 ms warm; same version ×3 → `stale`; `--bump` ×5 → 5 `Replaced`; 40k-pt synthetic (2.6 MB) decode 65 ms, round trip 73 ms; ping/pong; no server → exit 1. 38 unit tests incl. real-pipe round trip and second-server `busy` |
| 2026-10-11 | 3.1–3.7 | ✅ | in Revit 27.2.0.39 (from the log): listening at start; eg.json decode 0.4–15 ms, pipe transit 0.6–13 ms; same version ×3 → `Stale`; `--bump` ×5 → each `Replaced` the previous; 90k-pt synthetic (6.0 MB) ×5 decode 52–79 ms, transit 56–86 ms, every one superseding the last; ping 16 ms. User reported done |
| 2026-10-11 | 3.8–3.9 | ⏳ | not in the log: no second-session `pipe.busy`, no `stop` |
| 2026-10-11 | 4.1 | ✅ | `apply.unlinked` → error ack |
| 2026-10-11 | 4.2 | ✅ (with a bug) | `coords.set` E 383042 N 5776040; centre lands 0.5 m from origin. Toposolid created by `Toposolid.Create` with all points, including edge points (no fallback needed). **Bug:** repeated Link clicks created 4 toposolids (2785, 2854, 3021, 3134), some overlapping → guard added |
| 2026-10-11 | frame | ✅ | `frameInverted: true`: `ProjectLocation.GetTotalTransform()` is **shared→internal**; `sharedOfOriginFt` = 383042.000 / 5776040.000 m exactly |
| 2026-10-11 | 4.5, 4.7, 4.8 | ✅ (partly) | in place on element 3021, twice; undo cleared the applied memory (`applied.forgotten`). 307 + 132 pts → 373 applied, 66 = boundary (34 "outside" + 32 near-edge by float noise; now all counted as edge). `addZDeltaMaxMm: 0`: **AddPoints Z is absolute**. Apply 45–106 ms; send→commit 57–117 ms |
| 2026-10-11 | 4.5 bug | ❌→fix | `corners: 0`: `SlabShapeVertices` was empty right after `ResetSlabShape`+`Enable`, so the edge heights were never set (edges likely flat). Fix: `doc.Regenerate()` after `Enable`. **Needs re-test** |
| 2026-10-11 | 4.3, 4.9–4.12 | ⏳ | spot elevations not reported; others skipped by the user |
| 2026-10-11 | 4.5 re-test | ✅ | after `Regenerate()`: `corners: 66, cornersSet: 66, cornerZDeltaMaxMm: 0`; 373 interior + 66 edge, 0 outside. The vertex offset in `ModifySubElement` is relative to the reset flat top (as assumed). Apply 102–257 ms. Relink of the existing toposolid (3021) applied too |
| 2026-10-11 | 5.1–5.3 | ✅ | Civil connected 1 ms after start (Revit already listening). LSSYNC (forced) → applied on 3021, 373 + 66 corners, `cornerZDeltaMaxMm: 0`; first apply 528 ms (cold), send→commit 558 ms |
| 2026-10-11 | 5.4 | ✅ | User: "impressed with how it works". AECCEDITSURFACEPOINT edits → **edit→commit 1.31–2.02 s**. Breakdown: still in the command + 750 ms quiet = 1.2–1.9 s; extract 2–3 ms; pipe 0.4–0.7 ms; Revit wait 2–9 ms; Revit apply 69–130 ms. **From the moment the command ends, Revit has it in ≈ 0.85 s**; the rest is the user finishing the command |
| 2026-10-11 | 5.5 | ✅ | AECCADDSURFACEPOINT, 2 events in one command → one send after the command ended (309 / 550), edit→commit 2.73 s, Revit 72 ms |
| 2026-10-11 | 5.x | ✅ | edits that didn't change the geometry (`unchanged:true`, hash already acked) were **not** sent |
| 2026-10-11 | 5.10 | ✅ | Force Sync → `resync.request` → Civil extracted with `force` → applied even though unchanged. `revitWaitMs` 3.7–4.4 s = the time the "Asked Civil 3D…" dialog stayed open (the API thread can't apply under a modal dialog) |
| 2026-10-11 | 5.4 bug | ❌→fix | a point edited to **300 m** (rest of EG ≈ 500 m; also 400 / 510 / 520 / 600 m spikes) → `AddPoints` threw `InvalidOperationException: Could not add point … at index 105` on 3021 (created in step 4 without the pit). The transaction rolled back cleanly (toposolid kept its previous shape, error ack, Civil kept resending). Toposolid 3204, **created** from a payload already containing the 300 m point, accepted every later apply with the same pit. Not in the docs → fix: AddPoints now runs in a SubTransaction; on failure, the flat top (`Height Offset From Level`) is lowered to the lowest target and it retries once; flat top, thickness, target range and `baseLoweredFt` are logged; the error names the Civil point. **Needs re-test** |
| 2026-10-11 | 5.x bug | fix | a payload parked at 23:47:51 (3021 deleted) stayed parked after 3204 was linked; on the next project open it would have re-applied an **older** surface. Fix: apply skips (stale) a non-forced payload older than the applied version, and a successful apply clears an older parked one |
| 2026-10-12 | 5.13 | ✅ | fresh toposolid 3277 created without the pit: `flatTopFt` 1640.574 (= 500.05 m, the lowest EG point, so `Toposolid.Create` puts the flat top at the lowest point), thickness 0.984 ft (0.3 m). Point at 300 m → first `AddPoints` failed (`firstAddError` named Civil point X 383006.406 Y 5776073.754 el. 300.000), base lowered 656.322 ft (200 m), retry applied 375 + 66 corners, both Z deltas 0 mm, apply 215 ms. Next edit (with a 600 m spike): flat top stays at 300 m, applied directly, 80 ms. **Rule (measured): a point lower than the flat top − thickness can't be added; points above are fine** |
| 2026-10-12 | 5.x | ✅ | after Link created 3277, a same-hash resend → `skipped-same-hash` (Link records what it applied) |
| 2026-10-11 | 5.6–5.9, 5.11 | ⏳ | not in the log (no Revit restart, no undo-only round trip). Optional |
