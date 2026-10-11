using System.Text.Json;

namespace LiveSync.Shared;

/// <summary>
/// Append-only JSON Lines log, one file per app per UTC day: {app}-yyyyMMdd.jsonl.
/// Every line has "t" (UTC, 100 ns resolution), "app" and "stage", so lines from both
/// apps can be merged by time to measure latency. Never throws into the host app.
/// </summary>
public sealed class JsonlLog : IDisposable
{
    private readonly string _app;
    private readonly string _dir;
    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string? _currentDay;

    public JsonlLog(string app, string directory)
    {
        _app = app;
        _dir = directory;
    }

    public string? CurrentFile { get; private set; }

    /// <summary>Raised after each line is written (e.g. to echo to the Civil 3D command line). Called on the writer's thread.</summary>
    public event Action<string>? LineWritten;

    public void Write(string stage, object? fields = null)
    {
        var now = DateTime.UtcNow;
        try
        {
            var line = new Dictionary<string, object?>
            {
                ["t"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
                ["app"] = _app,
                ["stage"] = stage,
            };
            if (fields is not null)
            {
                foreach (var p in JsonSerializer.SerializeToElement(fields, MessageCodec.Options).EnumerateObject())
                    line[p.Name] = p.Value;
            }
            var json = JsonSerializer.Serialize(line, MessageCodec.Options);

            lock (_gate)
            {
                EnsureWriter(now);
                _writer!.WriteLine(json);
            }
            LineWritten?.Invoke(json);
        }
        catch
        {
            // Logging must never break the sync loop or the host.
        }
    }

    private void EnsureWriter(DateTime now)
    {
        var day = now.ToString("yyyyMMdd");
        if (_writer is not null && day == _currentDay) return;
        _writer?.Dispose();
        Directory.CreateDirectory(_dir);
        CurrentFile = Path.Combine(_dir, $"{_app}-{day}.jsonl");
        // FileShare.ReadWrite: tail the log while the app runs.
        var fs = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(fs) { AutoFlush = true };
        _currentDay = day;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
