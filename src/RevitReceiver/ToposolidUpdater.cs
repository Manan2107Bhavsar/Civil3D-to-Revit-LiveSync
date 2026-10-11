using System.Diagnostics;
using Autodesk.Revit.DB;
using LiveSync.Shared;

namespace LiveSync.Revit;

/// <summary>Outcome of one apply, for the ack and the log. Z deltas (ft) compare what Revit stored with what was sent.</summary>
public sealed class ApplyResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public int PointsSent { get; set; }
    public int PointsApplied { get; set; }
    public int Outside { get; set; }
    public int NearEdge { get; set; }
    public int Duplicate { get; set; }
    public int OnCorner { get; set; }
    public int Corners { get; set; }
    public int CornersSet { get; set; }
    public double AddZDeltaMaxFt { get; set; }
    public double CornerZDeltaMaxFt { get; set; }
    public double ResetMs { get; set; }
    public double AddMs { get; set; }
    public double CornersMs { get; set; }
    public double CommitMs { get; set; }
    /// <summary>Flat top after reset (ft, internal), and the range of heights asked for.</summary>
    public double? FlatTopFt { get; set; }
    public double? ThicknessFt { get; set; }
    public double? MinTargetFt { get; set; }
    public double? MaxTargetFt { get; set; }
    /// <summary>Set when AddPoints failed and the flat top was lowered by this much before the retry.</summary>
    public double? BaseLoweredFt { get; set; }
    public string? FirstAddError { get; set; }
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// Rewrites a toposolid's top surface in place: one Transaction, so one undo step, and the same
/// ElementId, type, material and hosted elements. Must run in a valid API context (ExternalEvent or command).
/// </summary>
public static class ToposolidUpdater
{
    /// <summary>Points closer than this to the sketch edge aren't added (AddPoints wants them strictly inside); edges take heights from the sketch vertices.</summary>
    private const double EdgeTolFt = 0.01; // 3 mm
    private const double DedupeFt = 0.005; // 1.5 mm
    private const double SnapFt = 0.005;

    public static ApplyResult Apply(Document doc, Toposolid topo, SurfaceUpdate u, CoordinateFrame frame)
    {
        var r = new ApplyResult();
        var sw = Stopwatch.StartNew();

        // Payload → internal feet (TIN topology is unchanged by the transform).
        double[] verts = frame.ToInternal(u.Vertices);
        double[] all = [.. verts, .. frame.ToInternal(u.ExtraPoints)];
        r.PointsSent = all.Length / 3;
        var tin = new TinInterpolator(verts, u.Triangles, SnapFt);
        var region = SketchRegion(doc, topo);

        using var tx = new Transaction(doc, $"LiveSync: {u.Source.Name} v{u.Version}");
        var failures = new FailureCollector();
        var opts = tx.GetFailureHandlingOptions();
        opts.SetFailuresPreprocessor(failures);
        opts.SetClearAfterRollback(true);
        tx.SetFailureHandlingOptions(opts);
        tx.Start();
        try
        {
            var editor = topo.GetSlabShapeEditor();
            editor.ResetSlabShape(); // also disables the editor
            editor.Enable();
            // Step 4 test: without a regenerate here SlabShapeVertices came back empty (corners: 0),
            // so the edge heights were never set. After a reset + regenerate, the only vertices
            // should be the sketch's own (corners), at the flat top.
            doc.Regenerate();
            editor = topo.GetSlabShapeEditor();
            var corners = editor.SlabShapeVertices.Cast<SlabShapeVertex>().Select(v => v.Position).ToList();
            r.Corners = corners.Count;
            if (corners.Count == 0) r.Warnings.Add("no sketch vertices exposed after reset + regenerate; edge heights not set");
            r.FlatTopFt = corners.Count > 0 ? corners.Min(c => c.Z) : null;
            r.ThicknessFt = topo.get_Parameter(BuiltInParameter.TOPOSOLID_ATTR_THICKNESS_PARAM)?.AsDouble();
            r.ResetMs = Lap(sw);

            var plan = ToposolidPlanner.Plan(all, region, EdgeTolFt, DedupeFt, corners.Select(c => (c.X, c.Y)));
            r.Outside = plan.Outside;
            r.NearEdge = plan.NearEdge;
            r.Duplicate = plan.Duplicate;
            r.OnCorner = plan.NearExisting;

            var points = plan.Interior.Select(i => new XYZ(all[3 * i], all[3 * i + 1], all[3 * i + 2])).ToList();
            var targetZ = points.Select(p => p.Z)
                .Concat(corners.Select(c => tin.ZAt(c.X, c.Y)).OfType<double>()).ToList();
            if (targetZ.Count > 0) { r.MinTargetFt = targetZ.Min(); r.MaxTargetFt = targetZ.Max(); }

            if (points.Count > 0)
            {
                IList<SlabShapeVertex> added;
                try
                {
                    added = AddPointsAtomic(doc, editor, points);
                }
                catch (Exception ex) when (r.FlatTopFt is { } flat && r.MinTargetFt is { } low && low < flat - 0.01)
                {
                    // Step 5 test: a pit 200 m below the rest of the surface threw "Could not add point"
                    // on a toposolid made before the pit existed, while one created with the pit
                    // accepted the same point. The docs give no rule, so: on failure, lower the flat
                    // top (Height Offset From Level) to the lowest target and try once more.
                    r.FirstAddError = Describe(ex, plan, u);
                    var offset = topo.get_Parameter(BuiltInParameter.TOPOSOLID_HEIGHTABOVELEVEL_PARAM);
                    if (offset is null || offset.IsReadOnly) throw new InvalidOperationException(r.FirstAddError, ex);
                    r.BaseLoweredFt = flat - low;
                    offset.Set(offset.AsDouble() - r.BaseLoweredFt.Value);
                    doc.Regenerate();
                    editor = topo.GetSlabShapeEditor();
                    if (!editor.IsEnabled) { editor.Enable(); doc.Regenerate(); editor = topo.GetSlabShapeEditor(); }
                    corners = editor.SlabShapeVertices.Cast<SlabShapeVertex>().Select(v => v.Position).ToList();
                    r.Corners = corners.Count;
                    try
                    {
                        added = AddPointsAtomic(doc, editor, points);
                    }
                    catch (Exception ex2)
                    {
                        throw new InvalidOperationException(Describe(ex2, plan, u) + " (also after lowering the toposolid base)", ex2);
                    }
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(Describe(ex, plan, u), ex);
                }
                r.PointsApplied = added.Count;
                // Diagnostic: does Revit store the Z we sent (absolute), or treat it as something else?
                // Matched by XY because the returned order isn't documented.
                var sent = new Dictionary<(long, long), double>(points.Count);
                foreach (var p in points) sent[Key(p)] = p.Z;
                foreach (var v in added.Cast<SlabShapeVertex>())
                    if (sent.TryGetValue(Key(v.Position), out var z))
                        r.AddZDeltaMaxFt = MaxAbs(r.AddZDeltaMaxFt, v.Position.Z - z);
            }
            r.AddMs = Lap(sw);

            // Sketch corners: target Z from the Civil TIN at the corner. The vertex offset is taken
            // relative to the reset (flat) position; the read-back below checks that assumption.
            var flatZ = new Dictionary<(long, long), double>();
            foreach (var c in corners) flatZ[Key(c)] = c.Z;
            var targets = new List<(SlabShapeVertex V, double Z)>();
            foreach (var v in editor.SlabShapeVertices.Cast<SlabShapeVertex>().ToList())
            {
                if (!flatZ.TryGetValue(Key(v.Position), out var flat)) continue; // an added interior point
                var z = tin.ZAt(v.Position.X, v.Position.Y);
                if (z is null) { r.Warnings.Add($"no TIN height at corner ({v.Position.X:F2}, {v.Position.Y:F2})"); continue; }
                editor.ModifySubElement(v, z.Value - flat);
                targets.Add((v, z.Value));
                r.CornersSet++;
            }
            r.CornersMs = Lap(sw);

            var status = tx.Commit();
            r.CommitMs = Lap(sw);
            r.Warnings.AddRange(failures.Messages);
            if (status != TransactionStatus.Committed)
            {
                r.Error = $"transaction {status}: " + string.Join("; ", failures.Messages);
                return r;
            }

            foreach (var (v, z) in targets)
                if (v.IsValidObject) r.CornerZDeltaMaxFt = MaxAbs(r.CornerZDeltaMaxFt, v.Position.Z - z);
            r.Ok = true;
            return r;
        }
        catch (Exception ex)
        {
            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
            r.Error = ex is InvalidOperationException { InnerException: { } } ? ex.Message : ex.GetType().Name + ": " + ex.Message;
            r.Warnings.AddRange(failures.Messages);
            return r;
        }
    }

    /// <summary>
    /// Creates a toposolid from a payload (Link Toposolid with nothing selected): sketch = Civil outer
    /// boundary, top = all payload points. Caller owns the transaction.
    /// </summary>
    public static Toposolid Create(Document doc, SurfaceUpdate u, CoordinateFrame frame, ElementId typeId, ElementId levelId)
    {
        double tol = doc.Application.ShortCurveTolerance;
        double[] xy = frame.ToInternalXY(u.Boundary);
        var pts = new List<XYZ>();
        for (int i = 0; i + 1 < xy.Length; i += 2)
        {
            var p = new XYZ(xy[i], xy[i + 1], 0);
            if (pts.Count == 0 || pts[^1].DistanceTo(p) > tol) pts.Add(p);
        }
        if (pts.Count > 2 && pts[^1].DistanceTo(pts[0]) <= tol) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 3) throw new InvalidOperationException("Payload boundary has fewer than 3 usable points.");

        var curves = new List<Curve>();
        for (int i = 0; i < pts.Count; i++) curves.Add(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
        var loop = CurveLoop.Create(curves);

        double[] all = frame.ToInternal([.. u.Vertices, .. u.ExtraPoints]);
        var filter = new XyProximityFilter(DedupeFt);
        var points = new List<XYZ>(all.Length / 3);
        for (int i = 0; i + 2 < all.Length; i += 3)
            if (filter.TryAdd(all[i], all[i + 1])) points.Add(new XYZ(all[i], all[i + 1], all[i + 2]));

        try
        {
            return Toposolid.Create(doc, [loop], points, typeId, levelId);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
            // The docs don't say whether points on the sketch edge are accepted; retry with interior points only.
            var region = new PlanarRegion([xy]);
            var interior = points.Where(p => region.Contains(p.X, p.Y) && region.DistanceToEdge(p.X, p.Y) > EdgeTolFt).ToList();
            return Toposolid.Create(doc, [loop], interior, typeId, levelId);
        }
    }

    /// <summary>The toposolid's sketch loops, tessellated, in internal XY.</summary>
    public static PlanarRegion SketchRegion(Document doc, Toposolid topo)
    {
        var sketch = (Sketch)doc.GetElement(topo.SketchId);
        var loops = new List<double[]>();
        foreach (CurveArray arr in sketch.Profile)
        {
            var loop = new List<double>();
            foreach (Curve c in arr)
            {
                var t = c.Tessellate();
                // Each curve's last point is the next curve's first: skip it.
                for (int i = 0; i < t.Count - 1; i++) { loop.Add(t[i].X); loop.Add(t[i].Y); }
            }
            loops.Add([.. loop]);
        }
        return new PlanarRegion(loops);
    }

    /// <summary>AddPoints in a sub-transaction, so a throw part-way leaves no half-added points behind.</summary>
    private static IList<SlabShapeVertex> AddPointsAtomic(Document doc, SlabShapeEditor editor, IList<XYZ> points)
    {
        using var sub = new SubTransaction(doc);
        sub.Start();
        try
        {
            var added = editor.AddPoints(points);
            sub.Commit();
            return added;
        }
        catch
        {
            if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
            throw;
        }
    }

    /// <summary>Revit's message names the point in internal feet; add the Civil point it came from.</summary>
    private static string Describe(Exception ex, PointPlan plan, SurfaceUpdate u)
    {
        string msg = ex.GetType().Name + ": " + ex.Message;
        var m = System.Text.RegularExpressions.Regex.Match(ex.Message, @"at index (\d+)");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int k) || k >= plan.Interior.Count) return msg;
        int i = plan.Interior[k];
        int nv = u.Vertices.Length / 3;
        var (src, j) = i < nv ? (u.Vertices, i) : (u.ExtraPoints, i - nv);
        return msg + $" Civil point: X {src[3 * j]:F3}, Y {src[3 * j + 1]:F3}, elevation {src[3 * j + 2]:F3} {u.Units}"
                   + (i < nv ? "" : " (breakline)");
    }

    private static (long, long) Key(XYZ p) => ((long)Math.Round(p.X * 1000), (long)Math.Round(p.Y * 1000));
    private static double MaxAbs(double a, double b) => Math.Abs(b) > Math.Abs(a) ? b : a;
    private static double Lap(Stopwatch sw)
    {
        double ms = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        return Math.Round(ms, 2);
    }
}

/// <summary>Deletes warnings so no modal dialog stalls the sync; errors roll the transaction back (no dialog) and are reported.</summary>
internal sealed class FailureCollector : IFailuresPreprocessor
{
    public List<string> Messages { get; } = [];

    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
    {
        bool error = false;
        foreach (var f in accessor.GetFailureMessages())
        {
            var severity = f.GetSeverity();
            Messages.Add($"{severity}: {f.GetDescriptionText()}");
            if (severity == FailureSeverity.Warning) accessor.DeleteWarning(f);
            else error = true;
        }
        return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }
}
