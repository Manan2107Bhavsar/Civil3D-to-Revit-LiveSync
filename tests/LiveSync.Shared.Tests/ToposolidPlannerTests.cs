using LiveSync.Shared;

namespace LiveSync.Shared.Tests;

public class ToposolidPlannerTests
{
    // 10 x 10 square with a 2 x 2 hole at (4..6, 4..6).
    private static readonly PlanarRegion SquareWithHole = new([
        [0, 0, 10, 0, 10, 10, 0, 10],
        [4, 4, 6, 4, 6, 6, 4, 6],
    ]);

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(5, 5, false)]   // in the hole
    [InlineData(11, 5, false)]  // outside
    [InlineData(9.99, 9.99, true)]
    public void Contains_UsesEvenOdd(double x, double y, bool expected) =>
        Assert.Equal(expected, SquareWithHole.Contains(x, y));

    [Fact]
    public void DistanceToEdge_IncludesHoleEdges()
    {
        Assert.Equal(1, SquareWithHole.DistanceToEdge(1, 5), 9);
        Assert.Equal(1, SquareWithHole.DistanceToEdge(3, 5), 9);
    }

    [Fact]
    public void Plan_DropsOutside_NearEdge_Duplicates_AndExistingCorners()
    {
        double[] xyz =
        [
            2, 2, 100,       // keep
            2, 2.0001, 101,  // duplicate of the first (within 1 mm-ish)
            0, 0, 100,       // on a corner -> near edge
            0.0005, 5, 100,  // within edge tolerance
            20, 20, 100,     // outside
            5, 5, 100,       // in the hole
            8, 8, 100,       // on top of an existing vertex
            7, 2, 100,       // keep
        ];
        var plan = ToposolidPlanner.Plan(xyz, SquareWithHole, edgeTol: 0.001, dedupe: 0.001, existingXY: [(8, 8)]);

        Assert.Equal([0, 7], plan.Interior);
        Assert.Equal(2, plan.Outside);
        Assert.Equal(2, plan.NearEdge);
        Assert.Equal(1, plan.Duplicate);
        Assert.Equal(1, plan.NearExisting);
    }

    [Fact]
    public void Interpolator_SnapsToVertex_ElseInterpolates()
    {
        // Two triangles over the unit square, z = x + 2y.
        double[] v = [0, 0, 0, 1, 0, 1, 1, 1, 3, 0, 1, 2];
        int[] t = [0, 1, 2, 0, 2, 3];
        var tin = new TinInterpolator(v, t, snap: 0.001);

        Assert.Equal(3, tin.ZAt(1.0005, 1)!.Value, 9);       // snapped to vertex 2
        Assert.Equal(0.25 + 1.5, tin.ZAt(0.25, 0.75)!.Value, 9);
        Assert.Null(tin.ZAt(5, 5));
    }
}
