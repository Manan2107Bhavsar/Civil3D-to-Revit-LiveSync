using System.Text.Json.Serialization;

namespace LiveSync.Shared;

/// <summary>
/// Base of every message on the pipe. The JSON "type" property selects the concrete class,
/// so later phases add kinds (corridor.update, family.changed) without touching the transport.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(SurfaceUpdate), MessageTypes.SurfaceUpdate)]
[JsonDerivedType(typeof(Ack), MessageTypes.Ack)]
[JsonDerivedType(typeof(ResyncRequest), MessageTypes.ResyncRequest)]
[JsonDerivedType(typeof(Ping), MessageTypes.Ping)]
[JsonDerivedType(typeof(ErrorMessage), MessageTypes.Error)]
public abstract class Message
{
    public int Schema { get; set; } = MessageCodec.SchemaVersion;
}

public static class MessageTypes
{
    public const string SurfaceUpdate = "surface.update";
    public const string Ack = "ack";
    public const string ResyncRequest = "resync.request";
    public const string Ping = "ping";
    public const string Error = "error";
}

public static class AckStatus
{
    public const string Applied = "applied";
    public const string SkippedSameHash = "skipped-same-hash";
    public const string Error = "error";
}

public sealed class SourceInfo
{
    public string App { get; set; } = "";
    public string Dwg { get; set; } = "";
    public string Handle { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>Stage timestamps (UTC). Each side fills its own; Revit echoes Civil's back in the ack.</summary>
public sealed class Timing
{
    public DateTime? LastEditUtc { get; set; }
    public DateTime? ExtractedUtc { get; set; }
    public DateTime? SentUtc { get; set; }
    public DateTime? ReceivedUtc { get; set; }
    public DateTime? EventStartUtc { get; set; }
    public DateTime? CommittedUtc { get; set; }
}

/// <summary>Civil 3D → Revit: full replacement of one surface's points.</summary>
public sealed class SurfaceUpdate : Message
{
    public string MappingId { get; set; } = "";
    public SourceInfo Source { get; set; } = new();

    /// <summary>Monotonic per mapping. Revit ignores anything older than what it applied.</summary>
    public long Version { get; set; }

    /// <summary>New on every NETLOAD, so Revit can tell a restarted Civil 3D from a stale message.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>See <see cref="SurfaceHasher"/>.</summary>
    public string Hash { get; set; } = "";

    /// <summary>Units of every coordinate below: "m", "ft" or "usft".</summary>
    public string Units { get; set; } = LiveSync.Shared.Units.Metres;

    /// <summary>"shared" = Civil world XY equals Revit shared E/N.</summary>
    public string Frame { get; set; } = "shared";

    /// <summary>Flat [x0,y0,z0, x1,y1,z1, ...] TIN vertices.</summary>
    public double[] Vertices { get; set; } = [];

    /// <summary>Flat [a0,b0,c0, ...] indices into Vertices (visible triangles only).</summary>
    public int[] Triangles { get; set; } = [];

    /// <summary>Flat xyz of extra points densified along breaklines (not TIN vertices).</summary>
    public double[] ExtraPoints { get; set; } = [];

    /// <summary>Outer boundary as a closed flat [x0,y0, x1,y1, ...] loop (first point not repeated). Used to rewrite the toposolid sketch.</summary>
    public double[] Boundary { get; set; } = [];

    public bool Decimated { get; set; }
    public Timing Timing { get; set; } = new();
}

/// <summary>Revit → Civil 3D: result of applying (or skipping) a SurfaceUpdate.</summary>
public sealed class Ack : Message
{
    public string MappingId { get; set; } = "";
    public long Version { get; set; }
    public string Hash { get; set; } = "";
    public string Status { get; set; } = AckStatus.Applied;
    public int PointsApplied { get; set; }
    public int PointsDropped { get; set; }
    public bool BoundaryRewritten { get; set; }
    public string? Detail { get; set; }
    public Timing Timing { get; set; } = new();
}

/// <summary>Revit → Civil 3D: "send me a fresh payload" (Revit's Force Sync button). Null MappingId = all mappings.</summary>
public sealed class ResyncRequest : Message
{
    public string? MappingId { get; set; }
}

public sealed class Ping : Message
{
    public DateTime SentUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ErrorMessage : Message
{
    public string? MappingId { get; set; }
    public string Detail { get; set; } = "";
}
