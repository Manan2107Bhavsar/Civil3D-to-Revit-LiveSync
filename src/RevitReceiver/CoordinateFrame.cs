using Autodesk.Revit.DB;
using LiveSync.Shared;
using Units = LiveSync.Shared.Units;

namespace LiveSync.Revit;

/// <summary>
/// Payload coordinates (Civil world = Revit shared, payload units) → Revit internal feet.
/// internal = sharedToInternal × (payload × unitFactor).
/// </summary>
public sealed class CoordinateFrame
{
    /// <summary>Revit warns about geometry farther than ~20 miles from the internal origin; refuse well before that.</summary>
    public const double FarFromOriginFt = 10_000 / Units.MetresPerFoot; // 10 km

    private CoordinateFrame(Transform sharedToInternal, double unitFactor, bool inverted, XYZ sharedOfOrigin)
    {
        SharedToInternal = sharedToInternal;
        UnitFactor = unitFactor;
        UsedInverse = inverted;
        SharedOfInternalOrigin = sharedOfOrigin;
    }

    public Transform SharedToInternal { get; }
    public double UnitFactor { get; }

    /// <summary>For the log: which direction of GetTotalTransform turned out to be internal→shared.</summary>
    public bool UsedInverse { get; }

    /// <summary>Shared coordinates (ft) of the internal origin, i.e. the project's survey offset.</summary>
    public XYZ SharedOfInternalOrigin { get; }

    /// <summary>
    /// The docs don't state the direction of <c>ProjectLocation.GetTotalTransform()</c>, so it is
    /// checked against <c>GetProjectPosition(XYZ.Zero)</c>, which gives the shared E/N/elevation of the
    /// internal origin. Whichever of T or T⁻¹ maps the origin there is internal→shared.
    /// </summary>
    public static CoordinateFrame For(Document doc, string units)
    {
        var location = doc.ActiveProjectLocation;
        var total = location.GetTotalTransform();
        var pos = location.GetProjectPosition(XYZ.Zero);
        var expected = new XYZ(pos.EastWest, pos.NorthSouth, pos.Elevation);

        Transform internalToShared;
        bool inverted;
        if (total.OfPoint(XYZ.Zero).DistanceTo(expected) <= total.Inverse.OfPoint(XYZ.Zero).DistanceTo(expected))
        {
            internalToShared = total;
            inverted = false;
        }
        else
        {
            internalToShared = total.Inverse;
            inverted = true;
        }
        return new CoordinateFrame(internalToShared.Inverse, Units.ToRevitFeetFactor(units), inverted, expected);
    }

    public XYZ ToInternal(double x, double y, double z) =>
        SharedToInternal.OfPoint(new XYZ(x * UnitFactor, y * UnitFactor, z * UnitFactor));

    /// <summary>Flat xyz payload array → flat xyz internal feet.</summary>
    public double[] ToInternal(double[] xyz)
    {
        var r = new double[xyz.Length];
        for (int i = 0; i + 2 < xyz.Length; i += 3)
        {
            var p = ToInternal(xyz[i], xyz[i + 1], xyz[i + 2]);
            r[i] = p.X; r[i + 1] = p.Y; r[i + 2] = p.Z;
        }
        return r;
    }

    /// <summary>Flat xy payload loop → flat xy internal feet (z ignored).</summary>
    public double[] ToInternalXY(double[] xy)
    {
        var r = new double[xy.Length];
        for (int i = 0; i + 1 < xy.Length; i += 2)
        {
            var p = ToInternal(xy[i], xy[i + 1], 0);
            r[i] = p.X; r[i + 1] = p.Y;
        }
        return r;
    }
}
