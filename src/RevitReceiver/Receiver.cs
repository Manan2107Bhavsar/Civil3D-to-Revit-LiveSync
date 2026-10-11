using LiveSync.Shared;

namespace LiveSync.Revit;

/// <summary>Summary of the last surface payload received, for the Status button.</summary>
public sealed record LastReceived(string MappingId, long Version, int Vertices, int ExtraPoints, int Bytes,
    DateTime ReceivedUtc, double DecodeMs, double? TransitMs);

/// <summary>
/// Pipe messages → latest-wins mailbox. Runs on pipe threads and must not call the Revit API.
/// Step 3 only acknowledges receipt; step 4 adds the ExternalEvent that applies the mailbox
/// to the toposolid.
/// </summary>
public sealed class Receiver : IDisposable
{
    private readonly PipeServer _server;

    public Receiver(MappingConfig config, JsonlLog log, string revitBuild)
    {
        Config = config;
        Log = log;
        _server = new PipeServer(config.PipeName, OnMessage, log);
        Log.Write("start", new { sessionId = SessionId, revitBuild, configPath = LiveSyncPaths.ConfigPath, mappings = config.Mappings.Count });
    }

    public string SessionId { get; } = Guid.NewGuid().ToString("N")[..12];
    public MappingConfig Config { get; }
    public JsonlLog Log { get; }
    public PipeServer Server => _server;
    public LatestWinsMailbox<SurfaceUpdate> Mailbox { get; } = new(u => u.Version);
    public LastReceived? Last { get; private set; }
    public long Superseded => Interlocked.Read(ref _superseded);
    private long _superseded;

    public Task StartAsync() => _server.StartAsync();

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

        var result = Mailbox.Post(u.MappingId, u, out var displaced);
        if (result != LatestWinsMailbox<SurfaceUpdate>.PostResult.Added)
        {
            Interlocked.Increment(ref _superseded);
            Log.Write("mailbox.superseded", new
            {
                mappingId = u.MappingId,
                version = u.Version,
                result = result.ToString(),
                droppedVersion = displaced?.Version,
            });
        }

        // Step 3: delivery ack only. From step 4 the ack is sent after the Revit transaction commits.
        _ = r.Connection.SendAsync(new Ack
        {
            MappingId = u.MappingId,
            Version = u.Version,
            Hash = u.Hash,
            Status = result == LatestWinsMailbox<SurfaceUpdate>.PostResult.Stale ? AckStatus.Stale : AckStatus.Received,
            Detail = result == LatestWinsMailbox<SurfaceUpdate>.PostResult.Stale
                ? "older than a pending version; dropped"
                : "received and queued (step 3: not applied)",
            Timing = new Timing
            {
                LastEditUtc = u.Timing.LastEditUtc,
                ExtractedUtc = u.Timing.ExtractedUtc,
                SentUtc = u.Timing.SentUtc,
                ReceivedUtc = r.ReceivedUtc,
            },
        });
    }

    private static double Ms(TimeSpan t) => Math.Round(t.TotalMilliseconds, 3);

    public void Dispose()
    {
        _server.Dispose();
        Log.Write("stop", new { sessionId = SessionId, received = _server.MessagesReceived });
        Log.Dispose();
    }
}
