using LiveSync.Shared;

namespace LiveSync.Shared.Tests;

public class TinGeometryTests
{
    /// <summary>n x m grid of unit squares, each split into 2 triangles; optionally with clockwise winding.</summary>
    private static (double[] V, int[] T) Grid(int n, int m, bool clockwise = false, Func<int, int, bool>? skipCell = null)
    {
        var v = new List<double>();
        for (int j = 0; j <= m; j++)
        for (int i = 0; i <= n; i++)
            v.AddRange([383000 + i, 5776000 + j, 500]);
        int Id(int i, int j) => j * (n + 1) + i;
        var t = new List<int>();
        for (int j = 0; j < m; j++)
        for (int i = 0; i < n; i++)
        {
            if (skipCell?.Invoke(i, j) == true) continue;
            int a = Id(i, j), b = Id(i + 1, j), c = Id(i + 1, j + 1), d = Id(i, j + 1);
            if (clockwise) t.AddRange([a, c, b, a, d, c]);
            else t.AddRange([a, b, c, a, c, d]);
        }
        return (v.ToArray(), t.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Grid_HasOneCcwOuterLoop_WithAllPerimeterVertices(bool clockwise)
    {
        var (v, t) = Grid(4, 3, clockwise);
        var b = TinGeometry.Boundary(v, t);

        Assert.Single(b.Outers);
        Assert.Empty(b.Holes);
        Assert.Equal(2 * (4 + 3), b.Outer.Length);
        Assert.Equal(12.0, TinGeometry.SignedArea(v, b.Outer), 6); // CCW, area 4 x 3
    }

    [Fact]
    public void MissingCenterCell_GivesHole()
    {
        var (v, t) = Grid(3, 3, skipCell: (i, j) => i == 1 && j == 1);
        var b = TinGeometry.Boundary(v, t);

        Assert.Single(b.Outers);
        var hole = Assert.Single(b.Holes);
        Assert.Equal(4, hole.Length);
        Assert.Equal(-1.0, TinGeometry.SignedArea(v, hole), 6);
    }

    [Fact]
    public void DisjointIslands_LargestFirst()
    {
        // Row of 5 cells with cells 1 and 2 removed: a 1-cell island and a 2-cell island.
        var (v, t) = Grid(5, 1, skipCell: (i, _) => i is 1 or 2);
        var b = TinGeometry.Boundary(v, t);

        Assert.Equal(2, b.Outers.Count);
        Assert.Equal(2.0, TinGeometry.SignedArea(v, b.Outers[0]), 6);
        Assert.Equal(1.0, TinGeometry.SignedArea(v, b.Outers[1]), 6);
        Assert.Empty(b.Holes);
    }

    [Fact]
    public void Densify_EvenSpacing_ExcludesVertices()
    {
        var pts = TinGeometry.DensifyXY([(0, 0), (10, 0), (10, 2.5)], spacing: 1.0);

        // 10 m segment -> 9 interior points; 2.5 m segment -> ceil(2.5)=3 pieces -> 2 interior points
        Assert.Equal(11, pts.Count);
        Assert.Equal((1.0, 0.0), pts[0]);
        Assert.Equal((9.0, 0.0), pts[8]);
        Assert.Equal(10.0, pts[9].X, 9);
        Assert.Equal(2.5 / 3, pts[9].Y, 9);
        Assert.DoesNotContain(pts, p => p == (10.0, 0.0));
    }

    [Fact]
    public void Densify_SegmentShorterThanSpacing_AddsNothing()
    {
        Assert.Empty(TinGeometry.DensifyXY([(0, 0), (0.6, 0)], 1.0));
        Assert.Empty(TinGeometry.DensifyXY([(0, 0), (5, 0)], 0));
    }

    [Fact]
    public void ProximityFilter_RejectsNearPoints_AcrossCellEdges()
    {
        var f = new XyProximityFilter(0.25);
        Assert.True(f.TryAdd(383000.0, 5776000.0));
        Assert.False(f.TryAdd(383000.2, 5776000.0));   // 0.2 < 0.25
        Assert.False(f.TryAdd(382999.9, 5775999.9));   // neighbour cell, 0.14 away
        Assert.True(f.TryAdd(383000.3, 5776000.0));    // 0.3 away
    }
}
