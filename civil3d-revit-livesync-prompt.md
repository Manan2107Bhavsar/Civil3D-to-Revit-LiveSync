# Project: Civil 3D → Revit Live Surface Sync

## What I want to build

A desktop tool that keeps a Civil 3D surface and a Revit model in sync while both programs are open side by side on the same Windows machine. When I edit a TIN surface in Civil 3D (add/move points, edit breaklines, change a profile PVI that drives a corridor surface), the matching toposolid in Revit should update automatically within about 1–2 seconds of the edit being committed. No manual export, no "reload link."

Later phases will extend this to corridor surfaces/solids and to SolidWorks/Inventor → Revit family updates with a "this family changed — see difference" notification. Design the architecture so those can be added without a rewrite, but **only build Phase 1 now**.

## Context about me and my setup

- I'm an Engineering Design & Drafting Technology student with civil drafting experience. I know Civil 3D and Revit well as a user, and I've built automation before, but explain non-obvious API decisions in the docs.
- Civil 3D 2026 on Windows. I've built a Python (3.13) + pywin32 COM MCP server for Civil 3D before. Lessons from that: direct COM ModelSpace drawing methods failed in Civil 3D 2026; SendCommand/LISP was the reliable path; COM interface pointers can't be shared across threads. I've also loaded .NET DLLs with NETLOAD.
- I already have a **Revit MCP server running on TCP port 8080** (a Revit add-in that listens on 8080 and executes commands through the Revit API). Ask me for its location/repo so you can read it, and reuse its connection pattern where it makes sense instead of building a second Revit add-in from scratch.
- Check which Revit version I have before choosing APIs (Toposolid exists in Revit 2024+; older versions use TopographySurface).

## Architecture I'm expecting (challenge it if you see a better option)

1. **Civil 3D side — C# .NET plugin loaded with NETLOAD.** COM polling is too slow and unreliable for live events, so this part should be a .NET plugin that subscribes to surface modification events (e.g. object-modified events on the surface's ObjectId), not Python/COM.
2. **Debounce.** Wait ~500–1000 ms after the last modification before sending, so dragging or rebuilding doesn't flood Revit.
3. **Payload.** Extract the surface's points/triangles, transform to the shared coordinate system, and send to Revit over localhost (reuse my 8080 channel or a separate local socket/named pipe — explain the choice). Include a version number and a hash so unchanged surfaces aren't resent.
4. **Revit side.** Receive the payload and apply it inside a valid API context using the ExternalEvent pattern. Update the existing toposolid in place where possible (keep its ElementId, type, material, and anything hosted on it) rather than deleting and recreating.
5. **Mapping.** A small config file that maps a Civil 3D surface (by name/handle) to a Revit element, plus the coordinate transform (shared coordinates / base point offset, units).
6. **No AI in the real-time loop.** The sync path is plain code. AI (MCP tools) is only for later features like summarizing what changed.

## Phase 1 scope (build only this)

- One Civil 3D TIN surface → one Revit toposolid, one direction.
- Auto-update on edit with debounce.
- Basic logging on both sides (timestamps so I can measure end-to-end latency).
- Decimation/point-limit option for large surfaces, off by default.
- A manual "force sync now" command on both sides for debugging.

Out of scope for now: corridors, pipe networks, Revit → Civil 3D, SolidWorks/Inventor, UI polish.

## How I want you to work

1. **Start by planning, not coding.** Read my existing Revit MCP add-in, confirm my Civil 3D/Revit versions and .NET target frameworks, then write `docs/PLAN.md` with the architecture, a component diagram (Mermaid), the message format, and the open questions. Ask me the open questions before writing code.
2. Build in small, testable steps. After each step, tell me exactly how to test it in Civil 3D/Revit (which drawing, which command, what I should see).
3. Use a small test surface (a few hundred points) first. Only test larger surfaces once the loop works.
4. Verify Autodesk API calls against the official API docs rather than guessing. If an API doesn't exist or behaves differently than expected, stop and tell me.
5. Don't modify my existing Revit MCP server's behavior without asking. Extending it is fine if I approve.
6. Commit to git after each working step with clear messages.

## Repository layout and documentation

Keep everything organized and documented:

```
/docs
  PLAN.md            architecture, decisions, open questions
  SETUP.md           build/install/NETLOAD/add-in steps, from zero
  TESTING.md         manual test procedures and expected results
  DECISIONS.md       short log: decision, alternatives, why
  CHANGELOG.md
/src
  /Civil3dPlugin     C# NETLOAD plugin
  /RevitReceiver     Revit-side code (or changes to my existing add-in)
  /Shared            message format / shared models
/config
  mapping.example.json
/logs                (gitignored)
README.md            what it is, status, quick start
CLAUDE.md            project notes for future Claude Code sessions: conventions, gotchas, how to build/test
```

Keep `CLAUDE.md` updated as we learn things (API quirks, build steps, mistakes not to repeat), so future sessions don't rediscover them.

## Definition of done for Phase 1

I edit a surface in Civil 3D, and within ~2 seconds the linked Revit toposolid updates in place, correctly positioned, with the latency visible in the logs. Setup is reproducible from `docs/SETUP.md` on a clean machine.

Start by reading this file, then my existing Revit MCP add-in, then write `docs/PLAN.md` and ask me your questions.
