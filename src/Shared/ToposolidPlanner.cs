namespace LiveSync.Shared;

/// <summary>
/// A plan region bounded by closed XY loops (the toposolid sketch). Even-odd rule, so
/// holes are simply extra loops. Loops are flat [x0,y0, x1,y1, ...], first point not repeated.
/// </summary>
public sealed class PlanarRegion
{
    private readonly List<double[]> _loops;

    public PlanarRegion(IEnumerable<double[]> loops)
    {
        _loops = loops.Where(l => l.Length >= 6).ToList();
        if (_loops.Count == 0) throw new ArgumentException("Region needs at least one loop of 3+ points.");
    }

    public IReadOnlyList<double[]> Loops => _loops;

    public bool Contains(double x, double y)
    {
        bool inside = false;
        foreach (var l in _loops)
        {
            int n = l.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = l[2 * i], yi = l[2 * i + 1], xj = l[2 * j], yj = l[2 * j + 1];
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                    inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>Shortest XY distance from the point to any loop edge.</summary>
    public double DistanceToEdge(double x, double y)
    {
        double best = double.MaxValue;
        foreach (var l in _loops)
        {
            int n = l.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++)
                best = Math.Min(best, SegmentDistance(x, y, l[2 * j], l[2 * j + 1], l[2 * i], l[2 * i + 1]));
        }
        return best;
    }

    private static double SegmentDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        double t = len2 == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        double ex = ax + t * dx - px, ey = ay + t * dy - py;
        return Math.Sqrt(ex * ex + ey * ey);
    }
}

/// <summary>Z of a TIN at any XY: snaps to a vertex if one is within tolerance, else interpolates in the containing triangle.</summary>
public sealed class TinInterpolator
{
    private readonly double[] _v;
    private readonly int[] _t;
    private readonly double _snap;
    private readonly Dictionary<(long, long), List<int>> _grid = [];

    public TinInterpolator(double[] vertices, int[] triangles, double snap)
    {
        _v = vertices;
        _t = triangles;
        _snap = snap;
        for (int i = 0; i < vertices.Length / 3; i++)
        {
            var key = Cell(vertices[3 * i], vertices[3 * i + 1]);
            if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = [];
            list.Add(i);
        }
    }

    private (long, long) Cell(double x, double y) => ((long)Math.Floor(x / _snap), (long)Math.Floor(y / _snap));

    public double? ZAt(double x, double y)
    {
        var (cx, cy) = Cell(x, y);
        int best = -1;
        double bestD = _snap;
        for (long gx = cx - 1; gx <= cx + 1; gx++)
            for (long gy = cy - 1; gy <= cy + 1; gy++)
                if (_grid.TryGetValue((gx, gy), out var list))
                    foreach (int i in list)
                    {
                        double d = Math.Sqrt(Math.Pow(_v[3 * i] - x, 2) + Math.Pow(_v[3 * i + 1] - y, 2));
                        if (d <= bestD) { bestD = d; best = i; }
                    }
        if (best >= 0) return _v[3 * best + 2];

        // Not a vertex: rare (sketch corners come from the TIN boundary), so a linear scan is fine.
        for (int k = 0; k + 2 < _t.Length; k += 3)
        {
            int a = _t[k], b = _t[k + 1], c = _t[k + 2];
            double x1 = _v[3 * a], y1 = _v[3 * a + 1], x2 = _v[3 * b], y2 = _v[3 * b + 1], x3 = _v[3 * c], y3 = _v[3 * c + 1];
            double det = (y2 - y3) * (x1 - x3) + (x3 - x2) * (y1 - y3);
            if (Math.Abs(det) < 1e-12) continue;
            double l1 = ((y2 - y3) * (x - x3) + (x3 - x2) * (y - y3)) / det;
            double l2 = ((y3 - y1) * (x - x3) + (x1 - x3) * (y - y3)) / det;
            double l3 = 1 - l1 - l2;
            const double eps = -1e-9;
            if (l1 >= eps && l2 >= eps && l3 >= eps)
                return l1 * _v[3 * a + 2] + l2 * _v[3 * b + 2] + l3 * _v[3 * c + 2];
        }
        return null;
    }
}

/// <summary>Which payload points can go to <c>SlabShapeEditor.AddPoints</c>.</summary>
public sealed record PointPlan(List<int> Interior, int Outside, int NearEdge, int Duplicate, int NearExisting);

public static class ToposolidPlanner
{
    /// <summary>
    /// AddPoints needs points strictly inside the sketch and distinct in XY. Keeps points that are
    /// inside <paramref name="region"/> and more than <paramref name="edgeTol"/> from its edges
    /// (edge heights are set on the sketch vertices instead), and drops XY duplicates within
    /// <paramref name="dedupe"/>, including points on top of <paramref name="existingXY"/>
    /// (vertices the toposolid already has, i.e. sketch corners).
    /// </summary>
    /// <param name="xyz">Flat xyz in the same units/frame as the region.</param>
    public static PointPlan Plan(double[] xyz, PlanarRegion region, double edgeTol, double dedupe, IEnumerable<(double X, double Y)>? existingXY = null)
    {
        var filter = new XyProximityFilter(dedupe);
        foreach (var (x, y) in existingXY ?? []) filter.Add(x, y);

        var interior = new List<int>(xyz.Length / 3);
        int outside = 0, nearEdge = 0, duplicate = 0, nearExisting = 0;
        var existing = new XyProximityFilter(dedupe);
        foreach (var (x, y) in existingXY ?? []) existing.Add(x, y);

        for (int i = 0; i < xyz.Length / 3; i++)
        {
            double x = xyz[3 * i], y = xyz[3 * i + 1];
            // Edge first: a point on the boundary is "inside" or "outside" by float noise; either way it's an edge point.
            if (region.DistanceToEdge(x, y) <= edgeTol) { nearEdge++; continue; }
            if (!region.Contains(x, y)) { outside++; continue; }
            if (!filter.TryAdd(x, y))
            {
                if (existing.IsNear(x, y)) nearExisting++; else duplicate++;
                continue;
            }
            interior.Add(i);
        }
        return new PointPlan(interior, outside, nearEdge, duplicate, nearExisting);
    }
}
