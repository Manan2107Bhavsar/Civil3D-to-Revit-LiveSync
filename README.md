# Civil 3D → Revit LiveSync

**Edit a surface in Civil 3D, and the matching toposolid in Revit updates by itself in about 1–2 seconds.**

There's no exporting, no LandXML round-trips and no "reload link". Keep both programs open side by side. When you finish an edit in Civil 3D (raise a point, add points, paste a surface, undo, and so on), Revit reshapes the *same* toposolid in place. Its type, material and anything hosted on it stay as they are.

```
Civil 3D 2027                                    Revit 2027
TIN surface (you edit)  ── points, same PC ──▶  Toposolid (updates itself)
                        ◀──── "applied" ─────
```

> **Status: early version (v0.1).** It works and has been tested on real edits, but it's a student project and still in development. Keep backups of your files. See [Limitations](#limitations).

---

## What you need

| | |
|---|---|
| **Autodesk Civil 3D 2027** | Other years won't work |
| **Autodesk Revit 2027** | Other years won't work |
| **Windows 10 or 11** (64-bit) | Both programs on the **same computer** |

You don't need admin rights, programming tools or an internet connection.

---

## Install (about 2 minutes)

1. **Close Civil 3D and Revit.**
2. Go to the [**Releases page**](https://github.com/Manan2107Bhavsar/Civil3D-to-Revit-LiveSync/releases/latest) and download **`LiveSync-0.1.0.zip`** (under *Assets*).
   *Don't use the green "Code → Download ZIP" button. That gives you the source code, not the ready-to-use tool.*
3. Right-click the downloaded zip → **Extract All…** → **Extract**.
4. Open the extracted folder and double-click **`Install.bat`**.
   - If Windows shows a blue **"Windows protected your PC"** box, click **More info** → **Run anyway**. (This appears because the tool isn't from a big publisher.)
   - A black window lists what was installed. Press any key to close it.
5. **Start Revit.** When it asks about an add-in that isn't signed ("LiveSync Receiver"), click **Always Load**.
6. **Start Civil 3D.** If it asks whether to load `LiveSync.Civil3d.dll`, click **Always Load** (or **Load Once**).

**Check that it worked:**
- In **Revit**, the **Add-Ins** tab has a **LiveSync** panel with *Link Toposolid*, *Force Sync* and *Status*.
- In **Civil 3D**, type `LSSTATUS` and press Enter. The command line shows LiveSync's status.

---

## First use: connect a surface to a toposolid

You do this **once** per surface. After that, syncing is automatic.

1. **Revit:** open (or create) your project. Leave it open.
2. **Civil 3D:** open the drawing with your TIN surface.
3. **Civil 3D:** type **`LSLINK`** → click the surface → press **Enter** to accept the suggested name (for example `eg`).
4. **Civil 3D:** type **`LSSYNC`**. This sends the surface to Revit right away.
5. **Revit:** make sure nothing is selected, then click **Add-Ins → LiveSync → Link Toposolid**.
   - Revit creates a new toposolid from the surface and links it.
   - If Revit says the surface is far from the project's origin (normal for real-world coordinates), click **Yes**. Revit then places the surface near the project origin and keeps the real-world coordinates as shared coordinates.
   - *Already have a toposolid you want to use?* Select it first, then click **Link Toposolid**.

**Done.** Now edit the surface in Civil 3D. When the command ends, Revit updates about a second later. Civil 3D's command line confirms each update, for example:
`LiveSync [eg] v12: applied in Revit 1.31 s after edit`

### Everyday use

Just open both files. Civil 3D and Revit find each other automatically, and links are remembered between sessions.

- Revit only updates **after you finish a command** in Civil 3D, not partway through. This is on purpose, so you never see a half-finished surface.
- If Revit was closed while you edited, the latest surface is sent once Revit and the project are open again. If it doesn't appear, click **Force Sync**.
- **Revit not up to date?** Click **LiveSync → Force Sync** in Revit, or type `LSSYNC` in Civil 3D.

---

## Commands

**Civil 3D** (type at the command line)

| Command | What it does |
|---|---|
| `LSLINK` | Link a surface (or re-link it) |
| `LSSYNC` | Send the surface to Revit now |
| `LSSTATUS` | Show links, connection to Revit and the last update time |
| `LSUNLINK` | Remove a link |
| `LSRELOAD` | Re-read the settings file after you've edited it by hand |
| `LSDEBUG` | Extra logging, for troubleshooting |

**Revit** (Add-Ins tab → LiveSync)

| Button | What it does |
|---|---|
| **Link Toposolid** | Nothing selected: create a toposolid from the surface. One toposolid selected: link that one |
| **Force Sync** | Ask Civil 3D to send a fresh copy of the surface |
| **Status** | Show whether Civil 3D is connected and what was last applied |

---

## Troubleshooting

| Problem | Try this |
|---|---|
| No **LiveSync** panel in Revit | Run `Install.bat` again with Revit closed. Make sure you clicked **Always Load** when Revit started |
| `LSLINK` is an "Unknown command" in Civil 3D | Run `Install.bat` again with Civil 3D closed, then restart Civil 3D. Check that it's **Civil 3D** 2027, not plain AutoCAD |
| Civil 3D says *Revit not connected* | Start Revit and open the project. They reconnect automatically within a few seconds |
| Revit doesn't change after an edit | Make sure the Civil 3D command has finished (press **Esc** or **Enter**). If Revit is busy (in a tool or a dialog), the update happens as soon as you exit it |
| The toposolid is in the wrong place | Make sure you clicked **Yes** to the "far from origin" question when linking. Otherwise, delete the toposolid and use **Link Toposolid** again |
| Something else | Logs are in `%APPDATA%\C3dRevitLiveSync\logs` (paste that into File Explorer's address bar). Please [open an issue](https://github.com/Manan2107Bhavsar/Civil3D-to-Revit-LiveSync/issues) and attach them |

## Uninstall

Close Civil 3D and Revit, then double-click **`Uninstall.bat`** in the folder you extracted. Your settings stay in `%APPDATA%\C3dRevitLiveSync`. Delete that folder too if you want everything removed.

## Limitations

- Supports **one direction** (Civil 3D → Revit) and **TIN surfaces → toposolids** only.
- Only **2027** versions of Civil 3D and Revit are supported.
- If you change the **outer boundary** of the surface in Civil 3D, points outside the toposolid's original outline are skipped. For now, re-link (create a new toposolid) after a big boundary change.
- Revit builds its own triangles from the points. Breaklines are respected by adding extra points along them, so the result matches closely but not always triangle-for-triangle.
- Very large surfaces (hundreds of thousands of points) haven't been tested for speed yet.

---

## For developers

How it works: a .NET plugin in Civil 3D watches the linked surface. When a command ends, it extracts the points, triangles and breaklines and sends them over a local named pipe (`\\.\pipe\c3d-revit-livesync`, current Windows user only). A Revit add-in receives them and updates the toposolid in one transaction using `SlabShapeEditor`.

```
src/Shared          shared library: messages, framing, hashing, config, logging (net10.0, BCL only)
src/Civil3dPlugin   Civil 3D plugin (LSLINK, LSSYNC, change detection, extraction)
src/RevitReceiver   Revit add-in (pipe server, ExternalEvent, toposolid update)
tests/              xUnit tests for the shared library
tools/              test sender, test-surface generator, release packager
installer/          Install/Uninstall scripts and the Civil 3D bundle manifest
docs/               PLAN, SETUP, TESTING, DECISIONS, CHANGELOG
```

Build (needs the .NET 10 SDK, Civil 3D 2027 and Revit 2027 installed):

```powershell
dotnet test C3dRevitLiveSync.sln                                   # unit tests
dotnet build src\Civil3dPlugin\LiveSync.Civil3d.csproj             # dev build + deploy (see docs/SETUP.md)
dotnet build src\RevitReceiver\LiveSync.Revit.csproj
powershell -ExecutionPolicy Bypass -File tools\package-release.ps1 -Version 0.1.0   # release zip in dist\
```

Dev builds deploy to `%LOCALAPPDATA%\C3dRevitLiveSync\deploy\` and are loaded with `livesync-load.lsp`. **Don't load a dev build in Civil 3D while the installed bundle is present**, because the plugin would load twice. Run `Uninstall.bat` first. Full developer setup: [`docs/SETUP.md`](docs/SETUP.md). Test procedures: [`docs/TESTING.md`](docs/TESTING.md). Design decisions: [`docs/DECISIONS.md`](docs/DECISIONS.md).
