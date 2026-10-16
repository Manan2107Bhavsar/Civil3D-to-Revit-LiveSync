using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LiveSync.Shared;

/// <summary>
/// Content hash of a surface, used on both sides to skip unchanged surfaces.
/// Canonical form: coordinates quantised to <c>quantum</c> (default 0.1 mm/0.0001 ft), vertices sorted,
/// triangle indices remapped, each triangle rotated to start at its smallest index (winding kept),
/// triangles sorted. So the hash ignores vertex/triangle ordering and float noise, but changes
/// on any real edit, including a swapped edge with the same vertices.
/// </summary>
public static class SurfaceHasher
{
    public const double DefaultQuantum = 1e-4;

    public static string Compute(SurfaceUpdate s, double quantum = DefaultQuantum) =>
        Compute(s.Vertices, s.Triangles, s.ExtraPoints, s.Units, quantum);

    public static string Compute(double[] vertices, int[] triangles, double[] extraPoints, string units, double quantum = DefaultQuantum)
    {
        if (vertices.Length % 3 != 0) throw new ArgumentException("Vertices length must be a multiple of 3.", nameof(vertices));
        if (triangles.Length % 3 != 0) throw new ArgumentException("Triangles length must be a multiple of 3.", nameof(triangles));
        if (extraPoints.Length % 3 != 0) throw new ArgumentException("ExtraPoints length must be a multiple of 3.", nameof(extraPoints));
        if (quantum <= 0) throw new ArgumentOutOfRangeException(nameof(quantum));

        var q = Quantise(vertices, quantum);
        int n = q.Length / 3;

        // Sort vertex indices by quantised (x, y, z).
        var order = Enumerable.Range(0, n).ToArray();
        Array.Sort(order, (a, b) => CompareTriple(q, a, b));
        var newIndex = new int[n];
        for (int i = 0; i < n; i++) newIndex[order[i]] = i;

        int t = triangles.Length / 3;
        var tris = new (int A, int B, int C)[t];
        for (int i = 0; i < t; i++)
        {
            int a = Remap(triangles[3 * i], newIndex), b = Remap(triangles[3 * i + 1], newIndex), c = Remap(triangles[3 * i + 2], newIndex);
            // Rotate (not reorder) so winding direction survives canonicalisation.
            if (b < a && b <= c) (a, b, c) = (b, c, a);
            else if (c < a && c < b) (a, b, c) = (c, a, b);
            tris[i] = (a, b, c);
        }
        Array.Sort(tris);

        var extra = Quantise(extraPoints, quantum);
        var extraOrder = Enumerable.Range(0, extra.Length / 3).ToArray();
        Array.Sort(extraOrder, (a, b) => CompareTriple(extra, a, b));

        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[8];
        void Long(long v) { BinaryPrimitives.WriteInt64LittleEndian(buf, v); h.AppendData(buf); }
        void Int(int v) { BinaryPrimitives.WriteInt32LittleEndian(buf, v); h.AppendData(buf, 0, 4); }

        h.AppendData(System.Text.Encoding.UTF8.GetBytes(units ?? ""));
        Long(BitConverter.DoubleToInt64Bits(quantum));
        Int(n);
        foreach (int i in order) { Long(q[3 * i]); Long(q[3 * i + 1]); Long(q[3 * i + 2]); }
        Int(t);
        foreach (var (a, b, c) in tris) { Int(a); Int(b); Int(c); }
        Int(extraOrder.Length);
        foreach (int i in extraOrder) { Long(extra[3 * i]); Long(extra[3 * i + 1]); Long(extra[3 * i + 2]); }

#if NET9_0_OR_GREATER
        return "sha256:" + Convert.ToHexStringLower(h.GetHashAndReset());
#else
        return "sha256:" + Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
#endif
    }

    private static long[] Quantise(double[] values, double quantum)
    {
        var q = new long[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            if (!double.IsFinite(values[i])) throw new ArgumentException($"Non-finite coordinate at index {i}.");
            q[i] = (long)Math.Round(values[i] / quantum, MidpointRounding.AwayFromZero);
        }
        return q;
    }

    private static int CompareTriple(long[] q, int a, int b)
    {
        int c = q[3 * a].CompareTo(q[3 * b]);
        if (c != 0) return c;
        c = q[3 * a + 1].CompareTo(q[3 * b + 1]);
        return c != 0 ? c : q[3 * a + 2].CompareTo(q[3 * b + 2]);
    }

    private static int Remap(int index, int[] newIndex) =>
        (uint)index < (uint)newIndex.Length ? newIndex[index]
            : throw new ArgumentException($"Triangle index {index} out of range 0..{newIndex.Length - 1}.");
}
