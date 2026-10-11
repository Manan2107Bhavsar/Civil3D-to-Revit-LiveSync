using System.Collections.Concurrent;
using Autodesk.Revit.UI;
using LiveSync.Shared;

namespace LiveSync.Revit;

/// <summary>Summary of the last surface payload received, for the Status button.</summary>
public sealed record LastReceived(string MappingId, long Version, int Vertices, int ExtraPoints, int Bytes,
    DateTime ReceivedUtc, double DecodeMs, double? TransitMs);

/// <summary>A payload waiting for the Revit API thread, with the client to ack (null when re-applied locally).</summary>
public sealed record Pending(SurfaceUpdate Update, PipeConnection? Connection, bool Force = false);

/// <summary>What was last applied per mapping (in memory; lost on Revit restart, which just means one re-apply).</summary>
public sealed record AppliedState(long Version, string Hash, DateTime CommittedUtc, ApplyResult Result);

/// <summary>
/// Pipe messages → latest-wins mailbox → ExternalEvent. The pipe side runs on pool threads and
/// never calls the Revit API; <see cref="ApplyHandler"/> drains the mailbox on the API thread.
/// </summary>
public sealed class Receiver : IDisposable
{
    private readonly PipeServer _server;
    private ExternalEvent? _applyEvent;

    public Receiver(MappingConfig config, JsonlLog log, string revitBuild)
    {
        Config = config;
        Log = log;
        _server = new PipeServer(config.PipeName, OnMessage, log);
        Log.Write("start", new { sessionId = SessionId, revitBuild, configPath = LiveSyncPaths.ConfigPath, mappings = config.Mappings.Count });
    }

    public string SessionId { get; } = Guid.NewGuid().ToString("N")[..12];
    public MappingConfig Config { get; private set; }
    public JsonlLog Log { get; }
    public PipeServer Server => _server;
    public LatestWinsMailbox<Pending> Mailbox { get; } = new(p => p.Update.Version);
    public LastReceived? Last { get; private set; }
    public long Superseded => Interlocked.Read(ref _superseded);
    private long _superseded;

    /// <summary>Newest payload received per mapping, kept after it is applied (Link Toposolid creates from it).</summary>
    public ConcurrentDictionary<string, SurfaceUpdate> LatestPayload { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<string, AppliedState> Applied { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Must be called in a Revit API context (OnStartup).</summary>
    public void AttachApplyEvent(ExternalEvent ev) => _applyEvent = ev;

    public Task StartAsync() => _server.StartAsync();

    public void ReloadConfig()
    {
        Config = ConfigStore.Load();
        Log.Write("config.reload", new { mappings = Config.Mappings.Count });
    }

    /// <summary>Queue a payload for applying (from the pipe or from a command) and wake the API thread.</summary>
    public LatestWinsMailbox<Pending>.PostResult Enqueue(Pending p)
    {
        var result = Mailbox.Post(p.Update.MappingId, p, out var displaced);
        if (result != LatestWinsMailbox<Pending>.PostResult.Added)
        {
            Interlocked.Increment(ref _superseded);
            Log.Write("mailbox.superseded", new
            {
                mappingId = p.Update.MappingId,
                version = p.Update.Version,
                result = result.ToString(),
                droppedVersion = displaced?.Update.Version,
            });
            // The dropped one (the old pending item, or p itself if p is the older) still gets an
            // answer, so its sender isn't left waiting.
            if (displaced?.Connection is { } c)
                _ = c.SendAsync(MakeAck(displaced.Update, AckStatus.Stale, "superseded by a newer version before it was applied"));
        }
        if (result != LatestWinsMailbox<Pending>.PostResult.Stale) _applyEvent?.Raise();
        return result;
    }

    private void OnMessage(ReceivedMessage r)
    {
        switch (r.Message)
        {
            case SurfaceUpdate u:
                OnSurfaceUpdate(u, r);
                break;
            case Ping p:
                Log.Write("ping", new { clientId = r.Connection.Id, transitMs = Ms(r.ReceivedUtc - p.SentUtc) });
                _ = r.Connection.SendAsync(new Ping());
                break;
            default:
                Log.Write("unexpected", new { clientId = r.Connection.Id, type = r.Message.GetType().Name });
                break;
        }
    }

    private void OnSurfaceUpdate(SurfaceUpdate u, ReceivedMessage r)
    {
        u.Timing.ReceivedUtc = r.ReceivedUtc;
        double? transitMs = u.Timing.SentUtc is { } sent ? Ms(r.ReceivedUtc - sent) : null;
        int vertices = u.Vertices.Length / 3, extra = u.ExtraPoints.Length / 3;

        Last = new LastReceived(u.MappingId, u.Version, vertices, extra, r.Bytes, r.ReceivedUtc, r.DecodeMs, transitMs);
        Log.Write("received", new
        {
            mappingId = u.MappingId,
            version = u.Version,
            clientId = r.Connection.Id,
            sessionId = u.SessionId,
            hash = u.Hash,
            units = u.Units,
            vertices,
            triangles = u.Triangles.Length / 3,
            extraPoints = extra,
            boundaryPoints = u.Boundary.Length / 2,
            bytes = r.Bytes,
            decodeMs = r.DecodeMs,
            transitMs,
            known = Config.Find(u.MappingId) is not null,
        });

        LatestPayload.AddOrUpdate(u.MappingId, u, (_, old) => u.Version >= old.Version ? u : old);

        if (Applied.TryGetValue(u.MappingId, out var done) && u.Version <= done.Version)
        {
            _ = r.Connection.SendAsync(MakeAck(u, AckStatus.Stale, $"v{done.Version} already applied"));
            return;
        }
        Enqueue(new Pending(u, r.Connection));
    }

    public void ForgetApplied(string reason)
    {
        if (Applied.IsEmpty) return;
        Applied.Clear();
        Log.Write("applied.forgotten", new { reason });
    }

    public static Ack MakeAck(SurfaceUpdate u, string status, string? detail, ApplyResult? result = null) => new()
    {
        MappingId = u.MappingId,
        Version = u.Version,
        Hash = u.Hash,
        Status = status,
        Detail = detail,
        PointsApplied = result?.PointsApplied ?? 0,
        PointsDropped = result is null ? 0 : result.Outside + result.NearEdge + result.Duplicate,
        Timing = new Timing
        {
            LastEditUtc = u.Timing.LastEditUtc,
            ExtractedUtc = u.Timing.ExtractedUtc,
            SentUtc = u.Timing.SentUtc,
            ReceivedUtc = u.Timing.ReceivedUtc,
            EventStartUtc = u.Timing.EventStartUtc,
            CommittedUtc = u.Timing.CommittedUtc,
        },
    };

    private static double Ms(TimeSpan t) => Math.Round(t.TotalMilliseconds, 3);

    public void Dispose()
    {
        _server.Dispose();
        _applyEvent?.Dispose();
        Log.Write("stop", new { sessionId = SessionId, received = _server.MessagesReceived });
        Log.Dispose();
    }
}
