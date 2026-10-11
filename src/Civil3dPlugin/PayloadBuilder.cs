using System.Diagnostics;
using LiveSync.Shared;

namespace LiveSync.Civil3d;

/// <summary>Background-thread half of the pipeline: pure data in, SurfaceUpdate out. No AutoCAD types.</summary>
internal static class PayloadBuilder
{
    public sealed record Result(SurfaceUpdate Update, TinBoundary Boundary, double PrepMs);

    public static Result Build(SurfaceSnapshot snap, Mapping mapping, string dwg, string sessionId, long version, DateTime? lastEditUtc)
    {
        var sw = Stopwatch.StartNew();
        var boundary = TinGeometry.Boundary(snap.Vertices, snap.Triangles);

        var outer = boundary.Outer;
        var boundaryXy = new double[outer.Length * 2];
        for (int i = 0; i < outer.Length; i++)
        {
            boundaryXy[2 * i] = snap.Vertices[3 * outer[i]];
            boundaryXy[2 * i + 1] = snap.Vertices[3 * outer[i] + 1];
        }

        var update = new SurfaceUpdate
        {
            MappingId = mapping.Id,
            Source = new SourceInfo { App = "Civil3D 2027", Dwg = dwg, Handle = snap.Handle, Name = snap.Name },
            Version = version,
            SessionId = sessionId,
            Units = mapping.Transform.Units,
            Frame = "shared",
            Vertices = snap.Vertices,
            Triangles = snap.Triangles,
            ExtraPoints = snap.ExtraPoints,
            Boundary = boundaryXy,
            Decimated = false,
            Timing = new Timing { LastEditUtc = lastEditUtc, ExtractedUtc = snap.ExtractedUtc },
        };
        update.Hash = SurfaceHasher.Compute(update);
        return new Result(update, boundary, sw.Elapsed.TotalMilliseconds);
    }
}
