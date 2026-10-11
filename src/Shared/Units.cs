namespace LiveSync.Shared;

/// <summary>Length units used in payloads and mapping. Revit's internal unit is the international foot.</summary>
public static class Units
{
    public const string Metres = "m";
    public const string Feet = "ft";
    /// <summary>US survey foot: exactly 1200/3937 m (≈2 ppm longer than the international foot).</summary>
    public const string UsSurveyFeet = "usft";

    public const double MetresPerFoot = 0.3048;
    public const double MetresPerUsSurveyFoot = 1200.0 / 3937.0;

    public static double MetresPer(string unit) => unit switch
    {
        Metres => 1.0,
        Feet => MetresPerFoot,
        UsSurveyFeet => MetresPerUsSurveyFoot,
        _ => throw new ArgumentException($"Unknown unit '{unit}'. Use m, ft or usft.", nameof(unit)),
    };

    /// <summary>Factor to multiply a length in <paramref name="unit"/> by to get Revit internal feet.</summary>
    public static double ToRevitFeetFactor(string unit) => MetresPer(unit) / MetresPerFoot;
}
