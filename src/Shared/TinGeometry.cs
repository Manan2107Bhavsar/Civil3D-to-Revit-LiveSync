namespace LiveSync.Shared;

/// <summary>Boundary loops of a triangulation, as vertex indices.</summary>
/// <param name="Outers">Counter-clockwise loops (largest area first). Several = disjoint islands.</param>
/// <param name="Holes">Clockwise loops inside an outer (e.g. hide boundaries).</param>
public sealed record TinBoundary(IReadOnlyList<int[]> Outers, IReadOnlyList<int[]> Holes)
{
    public int[] Outer => Outers.Count > 0 ? Outers[0] : [];
}

/// <summary>Host-independent TIN helpers. Coordinates are flat arrays: vertices xyz, points xy.</summary>
public static class TinGeometry
{
    /// <summary>
    /// Boundary loops = edges used by exactly one triangle, chained head to tail.
    /// Triangles are first oriented counter-clockwise (Civil 3D does not guarantee winding),
    /// so outer loops come out CCW (positive area) and holes CW.
    /// </summary>
    public static TinBoundary Boundary(double[] vertices, int[] triangles)
    {
        var edgeUse = new Dictionary<(int, int), int>();
        var directed = new List<(int From, int To)>(triangles.Length);

        for (int i = 0; i < triangles.Length; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            double area2 = Cross(vertices, a, b, c);
            if (area2 == 0) continue; // degenerate
            if (area2 < 0) (b, c) = (c, b);
            foreach (var (u, w) in new[] { (a, b), (b, c), (c, a) })
            {
                var key = u < w ? (u, w) : (w, u);
                edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
                directed.Add((u, w));
            }
        }

        var next = new Dictionary<int, Stack<int>>();
        foreach (var (u, w) in directed)
        {
            if (edgeUse[u < w ? (u, w) : (w, u)] != 1) continue;
            if (!next.TryGetValue(u, out var list)) next[u] = list = new Stack<int>();
            list.Push(w);
        }

        var outers = new List<(int[] Loop, double Area)>();
        var holes = new List<(int[] Loop, double Area)>();
        foreach (var start in next.Keys.OrderBy(k => k).ToList())
        {
            while (next.TryGetValue(start, out var outs) && outs.Count > 0)
            {
                var loop = new List<int> { start };
                int cur = outs.Pop();
                int guard = directed.Count + 1;
                while (cur != start && guard-- > 0)
                {
                    loop.Add(cur);
                    if (!next.TryGetValue(cur, out var o) || o.Count == 0) break; // open chain: bad mesh
                    cur = o.Pop();
                }
                if (cur != start || loop.Count < 3) continue;
                var arr = loop.ToArray();
                double area = SignedArea(vertices, arr);
                (area > 0 ? outers : holes).Add((arr, area));
            }
        }

        return new TinBoundary(
            outers.OrderByDescending(x => x.Area).Select(x => x.Loop).ToList(),
            holes.OrderBy(x => x.Area).Select(x => x.Loop).ToList());
    }

    /// <summary>Shoelace area of a loop of vertex indices (positive = CCW).</summary>
    public static double SignedArea(double[] vertices, IReadOnlyList<int> loop)
    {
        double s = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            int p = loop[i], q = loop[(i + 1) % loop.Count];
            s += vertices[3 * p] * vertices[3 * q + 1] - vertices[3 * q] * vertices[3 * p + 1];
        }
        return s / 2;
    }

    /// <summary>
    /// Evenly spaced points strictly between the vertices of an XY polyline, at most
    /// <paramref name="spacing"/> apart. Polyline vertices themselves are not returned.
    /// </summary>
    public static List<(double X, double Y)> DensifyXY(IReadOnlyList<(double X, double Y)> polyline, double spacing)
    {
        var result = new List<(double, double)>();
        if (spacing <= 0) return result;
        for (int i = 0; i + 1 < polyline.Count; i++)
        {
            var (x0, y0) = polyline[i];
            var (x1, y1) = polyline[i + 1];
            double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            int pieces = (int)Math.Ceiling(len / spacing - 1e-9);
            for (int k = 1; k < pieces; k++)
            {
                double f = (double)k / pieces;
                result.Add((x0 + f * (x1 - x0), y0 + f * (y1 - y0)));
            }
        }
        return result;
    }

    private static double Cross(double[] v, int a, int b, int c) =>
        (v[3 * b] - v[3 * a]) * (v[3 * c + 1] - v[3 * a + 1]) - (v[3 * c] - v[3 * a]) * (v[3 * b + 1] - v[3 * a + 1]);
}

/// <summary>
/// Rejects points closer than <c>minDistance</c> (in XY) to any point already added.
/// Revit's SlabShapeEditor.AddPoints requires points distinct in XY, and near-coincident
/// points make sliver triangles. Uniform hash grid: O(1) per query.
/// </summary>
public sealed class XyProximityFilter(double minDistance)
{
    private readonly Dictionary<(long, long), List<(double X, double Y)>> _cells = new();
    private readonly double _d = minDistance > 0 ? minDistance : throw new ArgumentOutOfRangeException(nameof(minDistance));

    public void Add(double x, double y)
    {
        var key = Cell(x, y);
        if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new();
        list.Add((x, y));
    }

    public bool IsNear(double x, double y)
    {
        var (cx, cy) = Cell(x, y);
        double d2 = _d * _d;
        for (long i = cx - 1; i <= cx + 1; i++)
        for (long j = cy - 1; j <= cy + 1; j++)
        {
            if (!_cells.TryGetValue((i, j), out var list)) continue;
            foreach (var (px, py) in list)
                if ((px - x) * (px - x) + (py - y) * (py - y) < d2) return true;
        }
        return false;
    }

    /// <summary>Adds the point unless it is near an existing one; returns whether it was added.</summary>
    public bool TryAdd(double x, double y)
    {
        if (IsNear(x, y)) return false;
        Add(x, y);
        return true;
    }

    private (long, long) Cell(double x, double y) => ((long)Math.Floor(x / _d), (long)Math.Floor(y / _d));
}
