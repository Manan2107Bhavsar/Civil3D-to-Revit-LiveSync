using LiveSync.Shared;

namespace LiveSync.Shared.Tests;

public class SurfaceHasherTests
{
    // Unit square split along the 0-2 diagonal, at realistic UTM magnitudes.
    private static readonly double[] V =
    [
        383000, 5776000, 500.0,
        383010, 5776000, 501.0,
        383010, 5776010, 502.0,
        383000, 5776010, 503.0,
    ];
    private static readonly int[] T = [0, 1, 2, 0, 2, 3];

    private static string H(double[] v, int[] t, double[]? extra = null, string units = "m") =>
        SurfaceHasher.Compute(v, t, extra ?? [], units);

    [Fact]
    public void SameSurface_SameHash_AndFormat()
    {
        var h = H(V, T);
        Assert.Equal(h, H((double[])V.Clone(), (int[])T.Clone()));
        Assert.StartsWith("sha256:", h);
        Assert.Equal("sha256:".Length + 64, h.Length);
    }

    [Fact]
    public void VertexAndTriangleOrder_DoNotChangeHash()
    {
        // Vertices listed in reverse: old index i becomes 3 - i.
        // Old (0,1,2) -> (3,2,1); old (0,2,3) -> (3,1,0). Then swap triangle order
        // and rotate each one (same winding): (3,1,0) -> (0,3,1), (3,2,1) -> (2,1,3).
        double[] v = [.. V[9..12], .. V[6..9], .. V[3..6], .. V[0..3]];
        int[] t = [0, 3, 1, 2, 1, 3];
        Assert.Equal(H(V, T), H(v, t));
    }

    [Fact]
    public void FloatNoiseBelowQuantum_DoesNotChangeHash()
    {
        var noisy = V.Select(x => x + 1e-7).ToArray();
        Assert.Equal(H(V, T), H(noisy, T));
    }

    [Fact]
    public void ElevationEdit_ChangesHash()
    {
        var edited = (double[])V.Clone();
        edited[5] += 0.01; // raise vertex 1 by 10 mm
        Assert.NotEqual(H(V, T), H(edited, T));
    }

    [Fact]
    public void SwappedEdge_ChangesHash()
    {
        int[] swapped = [0, 1, 3, 1, 2, 3]; // same vertices, other diagonal
        Assert.NotEqual(H(V, T), H(V, swapped));
    }

    [Fact]
    public void ReversedWinding_ChangesHash()
    {
        int[] reversed = [0, 2, 1, 0, 3, 2];
        Assert.NotEqual(H(V, T), H(V, reversed));
    }

    [Fact]
    public void ExtraPointsAndUnits_AreIncluded()
    {
        Assert.NotEqual(H(V, T), H(V, T, extra: [383005, 5776005, 501.5]));
        Assert.NotEqual(H(V, T), H(V, T, units: "ft"));
    }

    [Fact]
    public void BadInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => H([1, 2], []));
        Assert.Throws<ArgumentException>(() => H(V, [0, 1, 9]));
        Assert.Throws<ArgumentException>(() => H([double.NaN, 0, 0], []));
    }
}
