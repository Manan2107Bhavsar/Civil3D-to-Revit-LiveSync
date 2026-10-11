using System.Text.Json;
using LiveSync.Shared;

namespace LiveSync.Shared.Tests;

public sealed class ConfigAndLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "livesync-tests-" + Guid.NewGuid().ToString("N"));

    public ConfigAndLogTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var c = ConfigStore.Load(Path.Combine(_dir, "nope.json"));
        Assert.Equal("c3d-revit-livesync", c.PipeName);
        Assert.Equal(750, c.DebounceMs);
        Assert.Empty(c.Mappings);
    }

    [Fact]
    public void ExampleConfig_InRepo_Parses()
    {
        var path = Path.Combine(RepoRoot(), "config", "mapping.example.json");
        var c = ConfigStore.Load(path);
        var m = Assert.Single(c.Mappings);
        Assert.Equal("eg-site", m.Id);
        Assert.Equal(Units.Metres, m.Transform.Units);
        Assert.False(m.Decimation.Enabled); // off by default (Phase 1 requirement)
    }

    [Fact]
    public void Update_PreservesOtherSidesFields()
    {
        var path = Path.Combine(_dir, "mapping.json");
        // Civil 3D writes its side...
        ConfigStore.Update(c => c.Mappings.Add(new Mapping { Id = "eg", Civil = { SurfaceHandle = "2A7F" } }), path);
        // ...then Revit writes its side of the same mapping.
        ConfigStore.Update(c => c.Find("EG")!.Revit.ToposolidUniqueId = "abc-123", path);

        var m = ConfigStore.Load(path).Find("eg")!;
        Assert.Equal("2A7F", m.Civil.SurfaceHandle);
        Assert.Equal("abc-123", m.Revit.ToposolidUniqueId);
        Assert.False(File.Exists(path + ".lock"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Update_ConcurrentWriters_LoseNothing()
    {
        var path = Path.Combine(_dir, "mapping.json");
        Parallel.For(0, 20, i => ConfigStore.Update(c => c.Mappings.Add(new Mapping { Id = $"m{i}" }), path, TimeSpan.FromSeconds(30)));
        Assert.Equal(20, ConfigStore.Load(path).Mappings.Count);
    }

    [Fact]
    public void LogDir_ExpandsEnvironmentVariables()
    {
        var c = new MappingConfig { LogDir = @"%TEMP%\x" };
        Assert.DoesNotContain("%", c.ResolvedLogDir);
    }

    [Fact]
    public void Units_Factors()
    {
        Assert.Equal(1 / 0.3048, Units.ToRevitFeetFactor("m"), 12);
        Assert.Equal(1.0, Units.ToRevitFeetFactor("ft"), 12);
        Assert.Equal(1.000002, Units.ToRevitFeetFactor("usft"), 6); // ~2 ppm
        Assert.Throws<ArgumentException>(() => Units.ToRevitFeetFactor("yd"));
    }

    [Fact]
    public void JsonlLog_WritesOneParsableLinePerCall()
    {
        string? echoed = null;
        using (var log = new JsonlLog("civil3d", _dir))
        {
            log.LineWritten += l => echoed = l;
            log.Write("debounced", new { mappingId = "eg", version = 3 });
            log.Write("sent");
        }

        var file = Assert.Single(Directory.GetFiles(_dir, "civil3d-*.jsonl"));
        var lines = File.ReadAllLines(file);
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("debounced", first.RootElement.GetProperty("stage").GetString());
        Assert.Equal(3, first.RootElement.GetProperty("version").GetInt32());
        Assert.EndsWith("Z", first.RootElement.GetProperty("t").GetString());
        Assert.Equal(lines[1], echoed);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "C3dRevitLiveSync.sln"))) dir = dir.Parent;
        // Build output lives in %LOCALAPPDATA%, outside the repo; fall back to the source path baked in at compile time.
        return dir?.FullName ?? Path.GetFullPath(Path.Combine(SourceDir(), "..", ".."));
    }

    private static string SourceDir([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
