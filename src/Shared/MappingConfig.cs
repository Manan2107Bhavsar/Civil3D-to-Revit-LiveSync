using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveSync.Shared;

/// <summary>Contents of mapping.json, shared by both plugins. See config/mapping.example.json.</summary>
public sealed class MappingConfig
{
    public string PipeName { get; set; } = "c3d-revit-livesync";
    public int DebounceMs { get; set; } = 750;
    /// <summary>May contain %ENV% variables. Null/empty = <see cref="LiveSyncPaths.DefaultLogDir"/>.</summary>
    public string? LogDir { get; set; }
    public List<Mapping> Mappings { get; set; } = [];

    [JsonIgnore]
    public string ResolvedLogDir =>
        string.IsNullOrWhiteSpace(LogDir) ? LiveSyncPaths.DefaultLogDir : Environment.ExpandEnvironmentVariables(LogDir);

    public Mapping? Find(string id) => Mappings.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}

public sealed class Mapping
{
    public string Id { get; set; } = "";
    public CivilSide Civil { get; set; } = new();
    public RevitSide Revit { get; set; } = new();
    public TransformSettings Transform { get; set; } = new();
    public DecimationSettings Decimation { get; set; } = new();
    /// <summary>Spacing of extra points along breaklines, in payload units. 0 disables.</summary>
    public double BreaklineDensify { get; set; } = 1.0;
}

public sealed class CivilSide
{
    public string DwgPath { get; set; } = "";
    /// <summary>Primary key: stable across sessions within one DWG.</summary>
    public string SurfaceHandle { get; set; } = "";
    /// <summary>Fallback and for humans.</summary>
    public string SurfaceName { get; set; } = "";
}

public sealed class RevitSide
{
    public string DocumentPath { get; set; } = "";
    /// <summary>Primary key: stable across save-as and worksharing.</summary>
    public string? ToposolidUniqueId { get; set; }
    public long? ElementId { get; set; }
}

public sealed class TransformSettings
{
    public string Units { get; set; } = LiveSync.Shared.Units.Metres;
    /// <summary>Extra shift (payload units) applied after shared→internal, for projects whose shared coordinates are not acquired from the DWG.</summary>
    public double[] Offset { get; set; } = [0, 0, 0];
    public double RotationDeg { get; set; }
}

public sealed class DecimationSettings
{
    public bool Enabled { get; set; }
    public int MaxPoints { get; set; } = 20000;
}

/// <summary>
/// Reads/writes mapping.json. Both apps write it (Civil 3D its civil part, Revit its revit part),
/// so updates take an exclusive lock on a sidecar file and re-read before modifying.
/// </summary>
public static class ConfigStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Returns defaults when the file does not exist.</summary>
    public static MappingConfig Load(string? path = null)
    {
        path ??= LiveSyncPaths.ConfigPath;
        if (!File.Exists(path)) return new MappingConfig();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<MappingConfig>(fs, JsonOptions) ?? new MappingConfig();
    }

    /// <summary>Atomic read-modify-write under a cross-process lock.</summary>
    public static MappingConfig Update(Action<MappingConfig> modify, string? path = null, TimeSpan? timeout = null)
    {
        path ??= LiveSyncPaths.ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var _ = AcquireLock(path + ".lock", timeout ?? TimeSpan.FromSeconds(5));

        var config = Load(path);
        modify(config);

        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(config, JsonOptions));
        File.Move(temp, path, overwrite: true);
        return config;
    }

    private static FileStream AcquireLock(string lockPath, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
    }
}
