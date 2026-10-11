using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LiveSync.Shared;
using Units = LiveSync.Shared.Units;

namespace LiveSync.Revit;

/// <summary>
/// Links a toposolid to the mapping of the last payload received.
/// One toposolid selected: link it and apply the last payload to it now.
/// Nothing selected: create a toposolid from the last payload and link that.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class LinkToposolidCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var rx = App.Current;
        var uidoc = commandData.Application.ActiveUIDocument;
        if (rx is null || uidoc is null)
        {
            TaskDialog.Show("LiveSync", "Open a project first (and check that the receiver started: Status).");
            return Result.Cancelled;
        }
        var doc = uidoc.Document;

        string? mappingId = rx.Last?.MappingId ?? (rx.Config.Mappings.Count == 1 ? rx.Config.Mappings[0].Id : null);
        if (mappingId is null)
        {
            TaskDialog.Show("LiveSync", "No surface received yet. Run LSSYNC in Civil 3D (from step 5) or the SendTool, then try again.");
            return Result.Cancelled;
        }
        rx.LatestPayload.TryGetValue(mappingId, out var payload);

        var selected = uidoc.Selection.GetElementIds().Select(doc.GetElement).OfType<Toposolid>().ToList();
        Toposolid topo;
        if (selected.Count > 1)
        {
            TaskDialog.Show("LiveSync", "Select one toposolid (or none, to create one).");
            return Result.Cancelled;
        }
        if (selected.Count == 1)
        {
            topo = selected[0];
        }
        else
        {
            if (payload is null)
            {
                TaskDialog.Show("LiveSync", $"Nothing selected and no payload for '{mappingId}' has been received in this Revit session, so there's nothing to create from. Send one, or select an existing toposolid.");
                return Result.Cancelled;
            }
            var created = CreateFromPayload(doc, payload, rx);
            if (created is null) return Result.Cancelled;
            topo = created;
        }

        ConfigStore.Update(c =>
        {
            var m = c.Find(mappingId);
            if (m is null) c.Mappings.Add(m = new Mapping { Id = mappingId });
            m.Revit.DocumentPath = doc.PathName;
            m.Revit.ToposolidUniqueId = topo.UniqueId;
            m.Revit.ElementId = topo.Id.Value;
        });
        rx.ReloadConfig();
        rx.Log.Write("link", new { mappingId, doc = doc.PathName, uniqueId = topo.UniqueId, elementId = topo.Id.Value, created = selected.Count == 0 });

        string note = "";
        if (selected.Count == 1)
        {
            rx.Applied.TryRemove(mappingId, out _);
            if (payload is not null)
            {
                rx.Enqueue(new Pending(payload, null, Force: true));
                note = $"\nApplying {mappingId} v{payload.Version} to it now.";
            }
        }
        else if (payload is not null)
        {
            // Created from this payload already; don't re-apply the same surface.
            rx.Applied[mappingId] = new AppliedState(payload.Version, payload.Hash, DateTime.UtcNow, new ApplyResult { Ok = true });
        }
        if (string.IsNullOrEmpty(doc.PathName))
            note += "\nThe project isn't saved yet: save it so the mapping records its path.";

        TaskDialog.Show("LiveSync", $"Toposolid {topo.Id.Value} linked to '{mappingId}'.{note}");
        return Result.Succeeded;
    }

    private static Toposolid? CreateFromPayload(Document doc, SurfaceUpdate u, Receiver rx)
    {
        var frame = CoordinateFrame.For(doc, u.Units);
        var (cx, cy) = BoundaryCentre(u);
        var centre = frame.ToInternal(cx, cy, 0);
        double distFt = Math.Sqrt(centre.X * centre.X + centre.Y * centre.Y);

        if (distFt > CoordinateFrame.FarFromOriginFt)
        {
            // Shared coordinates don't match Civil. Offer the equivalent of "Specify Coordinates at
            // Point" at the internal origin: E/N = the surface centre (whole metres), elevation 0 so Z stays absolute.
            double e = Math.Round(cx), n = Math.Round(cy);
            var td = new TaskDialog("LiveSync")
            {
                MainInstruction = "The surface is far from this project's origin",
                MainContent = $"With the current shared coordinates the Civil surface lands {distFt * Units.MetresPerFoot / 1000:F1} km from the internal origin. "
                              + "Normally you would acquire coordinates from the Civil 3D DWG.\n\n"
                              + $"Set this project's shared coordinates so the internal origin is E {e:F0}, N {n:F0} ({u.Units}), elevation 0, rotation 0?\n\n"
                              + "Only do this in a test project, or one that has no shared coordinates yet. It can be undone with Undo.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No,
            };
            if (td.Show() != TaskDialogResult.Yes) return null;

            using (var tx = new Transaction(doc, "LiveSync: set shared coordinates"))
            {
                tx.Start();
                double f = Units.ToRevitFeetFactor(u.Units);
                doc.ActiveProjectLocation.SetProjectPosition(XYZ.Zero, new ProjectPosition(e * f, n * f, 0, 0));
                tx.Commit();
            }
            frame = CoordinateFrame.For(doc, u.Units);
            centre = frame.ToInternal(cx, cy, 0);
            rx.Log.Write("coords.set", new { e, n, units = u.Units, frameInverted = frame.UsedInverse, centreFt = new[] { centre.X, centre.Y } });
            if (Math.Sqrt(centre.X * centre.X + centre.Y * centre.Y) > CoordinateFrame.FarFromOriginFt)
            {
                TaskDialog.Show("LiveSync", "Setting the shared coordinates didn't bring the surface near the origin. Nothing was created; please send me the revit log.");
                return null;
            }
        }

        var typeId = new FilteredElementCollector(doc).OfClass(typeof(ToposolidType)).FirstElementId();
        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();
        if (typeId == ElementId.InvalidElementId || level is null)
        {
            TaskDialog.Show("LiveSync", "The project needs at least one toposolid type and one level.");
            return null;
        }

        using var t = new Transaction(doc, $"LiveSync: create toposolid {u.Source.Name}");
        var failures = new FailureCollector();
        var opts = t.GetFailureHandlingOptions();
        opts.SetFailuresPreprocessor(failures);
        t.SetFailureHandlingOptions(opts);
        t.Start();
        Toposolid topo;
        try
        {
            topo = ToposolidUpdater.Create(doc, u, frame, typeId, level.Id);
        }
        catch (Exception ex)
        {
            t.RollBack();
            rx.Log.Write("create.failed", new { mappingId = u.MappingId, error = ex.ToString() });
            TaskDialog.Show("LiveSync", "Creating the toposolid failed:\n" + ex.Message);
            return null;
        }
        if (t.Commit() != TransactionStatus.Committed)
        {
            TaskDialog.Show("LiveSync", "Creating the toposolid was rolled back:\n" + string.Join("\n", failures.Messages));
            return null;
        }
        rx.Log.Write("created", new { mappingId = u.MappingId, version = u.Version, elementId = topo.Id.Value, level = level.Name, warnings = failures.Messages });
        return topo;
    }

    private static (double X, double Y) BoundaryCentre(SurfaceUpdate u)
    {
        double[] xy = u.Boundary.Length >= 6 ? u.Boundary : [.. u.Vertices.Where((_, i) => i % 3 != 2)];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i + 1 < xy.Length; i += 2)
        {
            minX = Math.Min(minX, xy[i]); maxX = Math.Max(maxX, xy[i]);
            minY = Math.Min(minY, xy[i + 1]); maxY = Math.Max(maxY, xy[i + 1]);
        }
        return ((minX + maxX) / 2, (minY + maxY) / 2);
    }
}
