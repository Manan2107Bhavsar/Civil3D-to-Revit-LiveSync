using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.Settings;
using LiveSync.Shared;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using TinSurface = Autodesk.Civil.DatabaseServices.TinSurface;

namespace LiveSync.Civil3d;

public sealed class Commands
{
    private static SyncController? Controller
    {
        get
        {
            if (SyncController.Instance is null) Ui.Write("LiveSync is not running (load error; see the command history).");
            return SyncController.Instance;
        }
    }

    /// <summary>Pick a TIN surface and write/refresh its mapping entry in mapping.json.</summary>
    [CommandMethod("LSLINK")]
    public void Link()
    {
        if (Controller is not { } ctl) return;
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        var ed = doc.Editor;
        if (!doc.IsNamedDrawing)
        {
            ed.WriteMessage("\nSave the drawing first: the mapping stores the DWG path.\n");
            return;
        }

        var peo = new PromptEntityOptions("\nSelect TIN surface to link: ");
        peo.SetRejectMessage("\nNot a TIN surface.");
        peo.AddAllowedClass(typeof(TinSurface), exactMatch: true);
        var per = ed.GetEntity(peo);
        if (per.Status != PromptStatus.OK) return;

        string name, handle;
        using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
        {
            var surf = (TinSurface)tr.GetObject(per.ObjectId, OpenMode.ForRead);
            name = surf.Name;
            handle = surf.Handle.ToString();
        }
        var units = DrawingUnits(doc.Database);

        var existing = ctl.Config.Mappings.FirstOrDefault(m =>
            SyncController.SamePath(m.Civil.DwgPath, doc.Name) && string.Equals(m.Civil.SurfaceHandle, handle, StringComparison.OrdinalIgnoreCase));
        var pso = new PromptStringOptions($"\nMapping id <{existing?.Id ?? Slug(name)}>: ") { AllowSpaces = false };
        var psr = ed.GetString(pso);
        if (psr.Status != PromptStatus.OK) return;
        var id = string.IsNullOrWhiteSpace(psr.StringResult) ? existing?.Id ?? Slug(name) : psr.StringResult.Trim();

        string? unitsChangedFrom = null;
        ConfigStore.Update(c =>
        {
            var m = c.Find(id);
            if (m is null) c.Mappings.Add(m = new Mapping { Id = id });
            else if (m.Transform.Units != units) unitsChangedFrom = m.Transform.Units;
            m.Civil.DwgPath = doc.Name;
            m.Civil.SurfaceHandle = handle;
            m.Civil.SurfaceName = name;
            m.Transform.Units = units; // the drawing's settings are authoritative
        });

        ctl.Log.Write("link", new { mappingId = id, dwg = doc.Name, handle, name, units });
        if (unitsChangedFrom is not null)
            ed.WriteMessage($"\nNote: mapping units changed from '{unitsChangedFrom}' to '{units}' to match drawing settings.");
        ctl.Reload();
        ed.WriteMessage($"\nLinked surface '{name}' (handle {handle}, units {units}) as mapping '{id}'.\nConfig: {LiveSyncPaths.ConfigPath}\n");
    }

    /// <summary>Force sync now: extract every linked surface in the current drawing, ignoring the unchanged-hash check.</summary>
    [CommandMethod("LSSYNC")]
    public void SyncNow()
    {
        if (Controller is not { } ctl) return;
        var doc = AcApp.DocumentManager.MdiActiveDocument;
        var states = ctl.States.Where(s => s.Attached && s.Doc == doc).ToList();
        if (states.Count == 0)
        {
            doc.Editor.WriteMessage("\nNo linked surface in this drawing. Run LSLINK (or LSSTATUS to see mappings).\n");
            return;
        }
        foreach (var s in states)
        {
            ctl.Log.Write("sync.forced", new { mappingId = s.MappingId });
            ctl.Process(s, lastEditUtc: null, force: true);
        }
        doc.Editor.WriteMessage($"\nLiveSync: forced sync of {string.Join(", ", states.Select(s => s.MappingId))}; result follows.\n");
    }

    [CommandMethod("LSUNLINK")]
    public void Unlink()
    {
        if (Controller is not { } ctl) return;
        var ed = AcApp.DocumentManager.MdiActiveDocument.Editor;
        var ids = ctl.Config.Mappings.Select(m => m.Id).ToList();
        if (ids.Count == 0) { ed.WriteMessage("\nNo mappings.\n"); return; }

        var psr = ed.GetString(new PromptStringOptions($"\nMapping id to remove [{string.Join("/", ids)}]: ") { AllowSpaces = false });
        if (psr.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(psr.StringResult)) return;
        var id = psr.StringResult.Trim();

        bool removed = false;
        ConfigStore.Update(c => removed = c.Mappings.RemoveAll(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) > 0);
        ctl.Log.Write("unlink", new { mappingId = id, removed });
        ctl.Reload();
        ed.WriteMessage(removed ? $"\nRemoved mapping '{id}'.\n" : $"\nNo mapping '{id}'.\n");
    }

    [CommandMethod("LSSTATUS")]
    public void Status()
    {
        if (Controller is not { } ctl) return;
        var ed = AcApp.DocumentManager.MdiActiveDocument.Editor;
        ed.WriteMessage($"\nLiveSync session {ctl.SessionId}  debounce {ctl.Config.DebounceMs} ms  debug events {(ctl.DebugEvents ? "ON" : "off")}");
        ed.WriteMessage($"\n  config: {LiveSyncPaths.ConfigPath}");
        ed.WriteMessage($"\n  log:    {ctl.Log.CurrentFile ?? ctl.Config.ResolvedLogDir}");
        var client = ctl.Client;
        ed.WriteMessage($"\n  Revit:  {(client is null ? "pipe not started" : client.IsConnected ? $"connected since {Local(client.ConnectedSinceUtc)}" : "not connected (retrying every 2 s; is Revit open with the LiveSync add-in?)")}");
        if (ctl.States.Count == 0) ed.WriteMessage("\n  No mappings. Run LSLINK.");
        foreach (var s in ctl.States)
        {
            var m = ctl.Config.Find(s.MappingId);
            var where = s.Attached ? $"attached to '{s.SurfaceName}' in {Path.GetFileName(s.Doc!.Name)}" : $"not attached ({Path.GetFileName(m?.Civil.DwgPath)} not open or surface missing)";
            ed.WriteMessage($"\n  [{s.MappingId}] {where}; units {m?.Transform.Units}; events {s.TotalEvents}; changes {s.Changes}; " +
                            $"last edit {Local(s.LastEditUtc)}; last change {Local(s.LastDebouncedUtc)}{(s.Dirty ? "; PENDING" : "")}");
            if (s.LastAck is { } a)
            {
                var e2c = a.Timing.CommittedUtc - a.Timing.LastEditUtc;
                ed.WriteMessage($"\n      last Revit reply: v{a.Version} {a.Status}" + (e2c is { } d ? $", {d.TotalSeconds:0.00} s after edit" : "") + (a.Detail is null ? "" : $" ({a.Detail})"));
            }
        }
        ed.WriteMessage("\n");
    }

    /// <summary>Re-read mapping.json after editing it by hand.</summary>
    [CommandMethod("LSRELOAD")]
    public void ReloadConfig()
    {
        if (Controller is not { } ctl) return;
        ctl.Reload();
        Status();
    }

    /// <summary>Toggle logging of every ObjectModified event (any object) to the JSONL log. Diagnostic; noisy.</summary>
    [CommandMethod("LSDEBUG")]
    public void ToggleDebug()
    {
        if (Controller is not { } ctl) return;
        ctl.DebugEvents = !ctl.DebugEvents;
        ctl.Log.Write("debug", new { on = ctl.DebugEvents });
        Ui.Write($"LiveSync debug event logging {(ctl.DebugEvents ? "ON" : "off")} -> {ctl.Log.CurrentFile}");
    }

    private static string DrawingUnits(Database db)
    {
        var uz = CivilDocument.GetCivilDocument(db).Settings.DrawingSettings.UnitZoneSettings;
        if (uz.DrawingUnits == DrawingUnitType.Meters) return Units.Metres;
        return uz.ImperialToMetricConversion == ImperialToMetricConversionType.UsSurveyFoot ? Units.UsSurveyFeet : Units.Feet;
    }

    private static string Slug(string name)
    {
        var s = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return s.Length == 0 ? "surface" : s;
    }

    private static string Local(DateTime? utc) => utc?.ToLocalTime().ToString("HH:mm:ss.fff") ?? "-";
}
