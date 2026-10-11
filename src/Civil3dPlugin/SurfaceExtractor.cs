using System.Diagnostics;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using LiveSync.Shared;
using TinSurface = Autodesk.Civil.DatabaseServices.TinSurface;
using TinSurfaceTriangle = Autodesk.Civil.DatabaseServices.TinSurfaceTriangle;
using SurfaceBreakline = Autodesk.Civil.DatabaseServices.SurfaceBreakline;

namespace LiveSync.Civil3d;

/// <summary>Plain-data copy of a surface. Safe to hand to a background thread.</summary>
internal sealed class SurfaceSnapshot
{
    public required string Name { get; init; }
    public required string Handle { get; init; }
    public required double[] Vertices { get; init; }
    public required int[] Triangles { get; init; }
    public required double[] ExtraPoints { get; init; }
    public int CivilPoints { get; init; }
    public int CivilTriangles { get; init; }
    public int Breaklines { get; init; }
    public int ExtraRejected { get; init; }
    public bool OutOfDate { get; init; }
    public double ReadMs { get; init; }
    public DateTime ExtractedUtc { get; init; }
}

/// <summary>
/// Reads a TIN surface into a <see cref="SurfaceSnapshot"/>. MAIN THREAD ONLY: Civil 3D objects
/// must never be touched from another thread (same rule learned with COM).
/// </summary>
internal static class SurfaceExtractor
{
    /// <returns>null with a reason when the surface can't be extracted right now.</returns>
    public static SurfaceSnapshot? Extract(Document doc, ObjectId surfaceId, Mapping mapping, out string? skipReason)
    {
        skipReason = null;
        var sw = Stopwatch.StartNew();

        using var docLock = doc.LockDocument();
        using var tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
        var surf = (TinSurface)tr.GetObject(surfaceId, OpenMode.ForRead);

        if (surf.IsOutOfDate && !surf.AutoRebuild)
        {
            skipReason = "surface is out of date and Rebuild-Automatic is off; rebuild it in Civil 3D (or turn on Rebuild - Automatic)";
            return null;
        }

        // Only visible triangles: Revit can't hide parts of a toposolid, so hidden areas
        // (hide boundaries) are left out entirely. Vertices are collected from those triangles
        // and deduplicated by exact XY (a TIN has one vertex per XY).
        var index = new Dictionary<(double, double), int>();
        var verts = new List<double>();
        var tris = new List<int>();
        int Idx(Point3d p)
        {
            if (!index.TryGetValue((p.X, p.Y), out int i))
            {
                i = index.Count;
                index[(p.X, p.Y)] = i;
                verts.Add(p.X); verts.Add(p.Y); verts.Add(p.Z);
            }
            return i;
        }
        foreach (TinSurfaceTriangle t in surf.GetTriangles(includeInvisible: false))
        {
            tris.Add(Idx(t.Vertex1.Location));
            tris.Add(Idx(t.Vertex2.Location));
            tris.Add(Idx(t.Vertex3.Location));
        }

        var (extra, breaklines, rejected) = DensifyBreaklines(surf, mapping.BreaklineDensify, verts);

        return new SurfaceSnapshot
        {
            Name = surf.Name,
            Handle = surf.Handle.ToString(),
            Vertices = verts.ToArray(),
            Triangles = tris.ToArray(),
            ExtraPoints = extra,
            CivilPoints = surf.GetGeneralProperties().NumberOfPoints,
            CivilTriangles = surf.GetTinProperties().NumberOfTriangles,
            Breaklines = breaklines,
            ExtraRejected = rejected,
            OutOfDate = surf.IsOutOfDate,
            ReadMs = sw.Elapsed.TotalMilliseconds,
            ExtractedUtc = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Extra points every <paramref name="spacing"/> along every enabled breakline. Z comes from
    /// the surface itself (FindElevationAtXY), not from interpolating breakline vertices, so the
    /// points lie exactly on the Civil TIN even where other breaklines cross. Points near an
    /// existing vertex or each other (closer than spacing/4) are dropped.
    /// </summary>
    private static (double[] Points, int Breaklines, int Rejected) DensifyBreaklines(TinSurface surf, double spacing, List<double> verts)
    {
        if (spacing <= 0) return ([], 0, 0);

        var filter = new XyProximityFilter(Math.Max(spacing / 4, 0.01));
        for (int i = 0; i < verts.Count; i += 3) filter.Add(verts[i], verts[i + 1]);

        var result = new List<double>();
        int count = 0, rejected = 0;
        var defs = surf.BreaklinesDefinition;
        for (int op = 0; op < defs.Count; op++)
        {
            var operation = defs[op];
            if (!operation.Enabled) continue;
            foreach (SurfaceBreakline bl in operation)
            {
                count++;
                var xy = new List<(double, double)>();
                foreach (Point3d p in bl.Vertices) xy.Add((p.X, p.Y));
                foreach (var (x, y) in TinGeometry.DensifyXY(xy, spacing))
                {
                    if (!filter.TryAdd(x, y)) { rejected++; continue; }
                    double z;
                    try { z = surf.FindElevationAtXY(x, y); }
                    catch (System.Exception) { rejected++; continue; } // outside surface / in a hidden area
                    result.Add(x); result.Add(y); result.Add(z);
                }
            }
        }
        return (result.ToArray(), count, rejected);
    }
}
