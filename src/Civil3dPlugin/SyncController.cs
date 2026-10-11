using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using LiveSync.Shared;
using Message = LiveSync.Shared.Message;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using TinSurface = Autodesk.Civil.DatabaseServices.TinSurface;

namespace LiveSync.Civil3d;

/// <summary>Per-mapping runtime state (not persisted).</summary>
internal sealed class MappingState(string mappingId)
{
    public string MappingId { get; } = mappingId;
    public Document? Doc { get; set; }
    public ObjectId SurfaceId { get; set; } = ObjectId.Null;
    public string SurfaceName { get; set; } = "";
    public bool Attached => Doc is not null && !SurfaceId.IsNull;

    // Debounce bookkeeping. Written by the ObjectModified handler and read by the timer, both on the main thread.
    public bool Dirty { get; set; }
    public DateTime? BurstStartUtc { get; set; }
    public DateTime? LastEditUtc { get; set; }
    public int BurstEvents { get; set; }
    public bool LoggedWaitingForCommand { get; set; }

    public long TotalEvents { get; set; }
    public long Changes { get; set; }
    public DateTime? LastDebouncedUtc { get; set; }

    // Payload pipeline. Version is assigned on the main thread; Pipeline chains background work
    // so payloads for one mapping are built and written strictly in version order.
    public long LastVersion { get; set; }
    public Task Pipeline { get; set; } = Task.CompletedTask;
    /// <summary>Only read/written inside Pipeline continuations (serial).</summary>
    public string? LastHash { get; set; }

    /// <summary>Guards Pipeline chaining (main thread and the pipe-connect callback both append).</summary>
    public object Gate { get; } = new();

    // Send state. Written from the pipeline and from the pipe thread (acks); references are atomic.
    /// <summary>Newest payload built, kept so it can be (re)sent when Revit connects.</summary>
    public volatile SurfaceUpdate? Latest;
    /// <summary>Hash Revit confirmed it has (applied or already had). Equal hash = nothing to send.</summary>
    public volatile string? LastAckedHash;
    /// <summary>Hash sent and not yet answered; cleared on error or disconnect so it is resent.</summary>
    public volatile string? LastSentHash;
    public volatile Ack? LastAck;
    /// <summary>Revit pressed Force Sync; handled by the timer on the main thread.</summary>
    public volatile bool ForceRequested;
}

/// <summary>One edit burst, reported once the drawing has been quiet for DebounceMs and no command is running.</summary>
internal sealed record ChangeDetected(MappingState State, DateTime BurstStartUtc, DateTime LastEditUtc, int Events);

/// <summary>
/// Watches every open drawing for modifications of linked surfaces and debounces them.
///
/// Detection: Database.ObjectModified, filtered by ObjectId. Civil 3D rewrites the surface
/// object whenever it rebuilds, so this one event covers point edits, breakline/definition
/// changes and corridor-driven rebuilds. The handler only stamps time; all work happens later.
///
/// Debounce: a WinForms timer created on the main thread ticks there every 100 ms, so it
/// can read AutoCAD objects later (step 2) without marshalling. Application.Idle is not used
/// because it stops firing once AutoCAD is completely idle, which would strand the last edit.
/// </summary>
internal sealed class SyncController : IDisposable
{
    public static SyncController? Instance { get; set; }

    private readonly Dictionary<Document, Database> _watchedDocs = new();
    private readonly Dictionary<string, MappingState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ObjectId, MappingState> _bySurfaceId = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };

    public SyncController()
    {
        Config = ConfigStore.Load();
        Log = new JsonlLog("civil3d", Config.ResolvedLogDir);
    }

    public string SessionId { get; } = Guid.NewGuid().ToString("N")[..12];
    public MappingConfig Config { get; private set; }
    public JsonlLog Log { get; }
    public bool DebugEvents { get; set; }
    public PipeClient? Client { get; private set; }
    public IReadOnlyCollection<MappingState> States => _states.Values;

    /// <summary>Messages from background threads, written to the command line by the timer (main thread).</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _uiMessages = new();

    public void Start()
    {
        var dm = AcApp.DocumentManager;
        dm.DocumentCreated += OnDocumentCreated;
        dm.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
        _timer.Tick += OnTick;
        _timer.Start();

        Log.Write("start", new { sessionId = SessionId, configPath = LiveSyncPaths.ConfigPath, mappings = Config.Mappings.Count, Config.DebounceMs });
        AttachAll();

        Client = new PipeClient(Config.PipeName, OnPipeMessage, Log, OnPipeConnection);
        Client.Start();
    }

    /// <summary>Re-reads mapping.json and re-resolves every open drawing.</summary>
    public void Reload()
    {
        Config = ConfigStore.Load();
        Log.Write("config.reload", new { mappings = Config.Mappings.Count });
        AttachAll();
    }

    private void AttachAll()
    {
        foreach (var doc in _watchedDocs.Keys.ToList()) Detach(doc);
        _states.Clear();
        _bySurfaceId.Clear();
        foreach (var m in Config.Mappings) _states[m.Id] = new MappingState(m.Id);
        foreach (Document doc in AcApp.DocumentManager) Attach(doc);
    }

    private void Attach(Document doc)
    {
        var db = doc.Database;
        if (!_watchedDocs.ContainsKey(doc))
        {
            db.ObjectModified += OnObjectModified;
            db.ObjectErased += OnObjectErased;
            _watchedDocs[doc] = db;
        }

        foreach (var m in Config.Mappings.Where(m => SamePath(m.Civil.DwgPath, doc.Name)))
        {
            var state = _states[m.Id];
            var id = ResolveSurface(db, m);
            if (id.IsNull)
            {
                Log.Write("attach.notfound", new { mappingId = m.Id, dwg = doc.Name, m.Civil.SurfaceHandle, m.Civil.SurfaceName });
                Ui.Write($"LiveSync [{m.Id}]: surface '{m.Civil.SurfaceName}' (handle {m.Civil.SurfaceHandle}) not found in {Path.GetFileName(doc.Name)}. Run LSLINK.");
                continue;
            }
            state.Doc = doc;
            state.SurfaceId = id;
            state.SurfaceName = m.Civil.SurfaceName;
            _bySurfaceId[id] = state;
            Log.Write("attach", new { mappingId = m.Id, dwg = doc.Name, handle = id.Handle.ToString(), m.Civil.SurfaceName });
        }
    }

    private void Detach(Document doc)
    {
        if (!_watchedDocs.Remove(doc, out var db)) return;
        try
        {
            db.ObjectModified -= OnObjectModified;
            db.ObjectErased -= OnObjectErased;
        }
        catch (System.Exception) { /* database already disposed */ }

        foreach (var s in _states.Values.Where(s => s.Doc == doc))
        {
            _bySurfaceId.Remove(s.SurfaceId);
            s.Doc = null;
            s.SurfaceId = ObjectId.Null;
            s.Dirty = false;
            Log.Write("detach", new { mappingId = s.MappingId });
        }
    }

    /// <summary>Handle first (stable within a DWG), then name. A name match updates the stored handle.</summary>
    private ObjectId ResolveSurface(Database db, Mapping m)
    {
        if (TryParseHandle(m.Civil.SurfaceHandle, out var handle)
            && db.TryGetObjectId(handle, out var id) && IsLiveTinSurface(id))
            return id;

        if (string.IsNullOrEmpty(m.Civil.SurfaceName)) return ObjectId.Null;

        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        foreach (ObjectId sid in Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(db).GetSurfaceIds())
        {
            if (!IsLiveTinSurface(sid)) continue;
            var surf = (TinSurface)tr.GetObject(sid, OpenMode.ForRead);
            if (!string.Equals(surf.Name, m.Civil.SurfaceName, StringComparison.OrdinalIgnoreCase)) continue;

            var newHandle = sid.Handle.ToString();
            Log.Write("attach.byname", new { mappingId = m.Id, oldHandle = m.Civil.SurfaceHandle, newHandle });
            ConfigStore.Update(c => { if (c.Find(m.Id) is { } cm) cm.Civil.SurfaceHandle = newHandle; });
            m.Civil.SurfaceHandle = newHandle;
            return sid;
        }
        return ObjectId.Null;
    }

    // --- Event handlers: keep these cheap; they run inside Civil 3D's own transactions. ---

    private void OnObjectModified(object sender, ObjectEventArgs e)
    {
        var id = e.DBObject.ObjectId;
        if (_bySurfaceId.TryGetValue(id, out var s))
        {
            var now = DateTime.UtcNow;
            if (!s.Dirty) { s.BurstStartUtc = now; s.BurstEvents = 0; s.LoggedWaitingForCommand = false; }
            s.Dirty = true;
            s.LastEditUtc = now;
            s.BurstEvents++;
            s.TotalEvents++;
        }
        if (DebugEvents)
        {
            // Exceptions must not escape into Civil 3D's transaction.
            try { Log.Write("event.modified", new { handle = id.Handle.ToString(), cls = id.ObjectClass?.Name, linked = s?.MappingId }); }
            catch (System.Exception) { }
        }
    }

    private void OnObjectErased(object sender, ObjectErasedEventArgs e)
    {
        var id = e.DBObject.ObjectId;
        if (!_bySurfaceId.TryGetValue(id, out var s)) return;
        Log.Write(e.Erased ? "surface.erased" : "surface.unerased", new { mappingId = s.MappingId });
        if (e.Erased) Ui.Write($"LiveSync [{s.MappingId}]: linked surface was erased; syncing paused until it is restored (UNDO) or relinked.");
        s.Dirty = !e.Erased && s.Dirty;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // An exception escaping a WinForms timer tick would surface as an unhandled
        // exception in Civil 3D; contain it per mapping and keep ticking.
        while (_uiMessages.TryDequeue(out var msg)) Ui.Write(msg);
        foreach (var s in _states.Values)
        {
            try { TickOne(s, DateTime.UtcNow); }
            catch (System.Exception ex) { Log.Write("error", new { mappingId = s.MappingId, where = "tick", error = ex.ToString() }); s.Dirty = false; }
        }
    }

    private void TickOne(MappingState s, DateTime now)
    {
        if (s.ForceRequested && s.Attached && string.IsNullOrEmpty(s.Doc!.CommandInProgress))
        {
            s.ForceRequested = false;
            Log.Write("sync.forced", new { mappingId = s.MappingId, by = "revit" });
            Ui.Write($"LiveSync [{s.MappingId}]: Force Sync requested by Revit.");
            Process(s, lastEditUtc: null, force: true);
            return;
        }
        if (!s.Dirty || s.Doc is null || s.LastEditUtc is null) return;
        if ((now - s.LastEditUtc.Value).TotalMilliseconds < Config.DebounceMs) return;
        if (s.SurfaceId.IsErased) { s.Dirty = false; return; }

        // Don't sync halfway through a grip-drag or an interactive edit command.
        var cmd = s.Doc.CommandInProgress;
        if (!string.IsNullOrEmpty(cmd))
        {
            if (!s.LoggedWaitingForCommand)
            {
                Log.Write("debounce.wait", new { mappingId = s.MappingId, command = cmd });
                s.LoggedWaitingForCommand = true;
            }
            return;
        }

        s.Dirty = false;
        s.Changes++;
        s.LastDebouncedUtc = now;
        var change = new ChangeDetected(s, s.BurstStartUtc!.Value, s.LastEditUtc.Value, s.BurstEvents);
        Log.Write("debounced", new
        {
            mappingId = s.MappingId,
            change = s.Changes,
            events = change.Events,
            burstMs = (change.LastEditUtc - change.BurstStartUtc).TotalMilliseconds,
            quietMs = (now - change.LastEditUtc).TotalMilliseconds,
            lastEditUtc = change.LastEditUtc,
        });
        Ui.Write($"LiveSync [{s.MappingId}]: change #{s.Changes} detected ({change.Events} events over {(change.LastEditUtc - change.BurstStartUtc).TotalMilliseconds:0} ms).");

        Process(s, change.LastEditUtc, force: false);
    }

    /// <summary>
    /// Extract (main thread) then build/hash/write (background, serial per mapping).
    /// force = LSSYNC: write even if the hash is unchanged.
    /// </summary>
    public void Process(MappingState s, DateTime? lastEditUtc, bool force)
    {
        if (!s.Attached || Config.Find(s.MappingId) is not { } mapping) return;
        var doc = s.Doc!;

        var snap = SurfaceExtractor.Extract(doc, s.SurfaceId, mapping, out var skip);
        if (snap is null)
        {
            Log.Write("extract.skipped", new { mappingId = s.MappingId, reason = skip });
            Ui.Write($"LiveSync [{s.MappingId}]: not synced - {skip}.");
            return;
        }

        long version = s.LastVersion = Math.Max(s.LastVersion + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        string dwg = doc.Name;
        Chain(s, () => Prepare(s, snap, mapping, dwg, version, lastEditUtc, force));
    }

    private static void Chain(MappingState s, Action work)
    {
        lock (s.Gate) s.Pipeline = s.Pipeline.ContinueWith(_ => work(), TaskScheduler.Default);
    }

    private void Prepare(MappingState s, SurfaceSnapshot snap, Mapping mapping, string dwg, long version, DateTime? lastEditUtc, bool force)
    {
        try
        {
            var r = PayloadBuilder.Build(snap, mapping, dwg, SessionId, version, lastEditUtc);
            var u = r.Update;
            u.Force = force;
            bool unchanged = u.Hash == s.LastHash;
            s.LastHash = u.Hash;
            s.Latest = u;

            string? file = null;
            if (!unchanged || force)
            {
                var dir = Path.Combine(LiveSyncPaths.AppDataDir, "payloads");
                Directory.CreateDirectory(dir);
                file = Path.Combine(dir, $"{mapping.Id}.json");
                File.WriteAllBytes(file, MessageCodec.Serialize(u));
            }

            Log.Write("extracted", new
            {
                mappingId = mapping.Id,
                version,
                force,
                unchanged,
                vertices = u.Vertices.Length / 3,
                triangles = u.Triangles.Length / 3,
                extraPoints = u.ExtraPoints.Length / 3,
                extraRejected = snap.ExtraRejected,
                breaklines = snap.Breaklines,
                boundaryPoints = u.Boundary.Length / 2,
                outerLoops = r.Boundary.Outers.Count,
                holes = r.Boundary.Holes.Count,
                civilPoints = snap.CivilPoints,
                civilTriangles = snap.CivilTriangles,
                snap.OutOfDate,
                readMs = snap.ReadMs,
                prepMs = r.PrepMs,
                hash = u.Hash,
                file,
            });

            var msg = $"LiveSync [{mapping.Id}]: {u.Vertices.Length / 3} pts / {u.Triangles.Length / 3} tri " +
                      $"(Civil: {snap.CivilPoints} pts / {snap.CivilTriangles} tri), +{u.ExtraPoints.Length / 3} breakline pts " +
                      $"from {snap.Breaklines} breaklines, boundary {u.Boundary.Length / 2} pts" +
                      (r.Boundary.Outers.Count > 1 || r.Boundary.Holes.Count > 0 ? $" ({r.Boundary.Outers.Count} outer, {r.Boundary.Holes.Count} holes; only the largest outer is used)" : "") +
                      $"; read {snap.ReadMs:0} ms, prep {r.PrepMs:0} ms; v{version} " +
                      (unchanged ? (force ? "UNCHANGED (written anyway)" : "UNCHANGED, skipped") : "") +
                      (force && file is not null ? $"-> {file}" : "");
            _uiMessages.Enqueue(msg);
            Send(s, u, force);
        }
        catch (System.Exception ex)
        {
            Log.Write("error", new { mappingId = mapping.Id, where = "prepare", version, error = ex.ToString() });
            _uiMessages.Enqueue($"LiveSync [{mapping.Id}]: payload build failed: {ex.Message}");
        }
    }

    // --- Pipe to Revit. Send runs on the mapping's pipeline thread; the callbacks on a pipe thread. ---

    /// <summary>Sends unless Revit already has this surface (acked) or it's already on its way. Blocks the pipeline thread so sends stay in version order.</summary>
    private void Send(MappingState s, SurfaceUpdate u, bool force)
    {
        if (!force && (u.Hash == s.LastAckedHash || u.Hash == s.LastSentHash)) return;
        if (Client is null || !Client.IsConnected)
        {
            Log.Write("send.offline", new { mappingId = s.MappingId, version = u.Version });
            _uiMessages.Enqueue($"LiveSync [{s.MappingId}]: Revit not connected; v{u.Version} will be sent when it connects.");
            return;
        }
        u.Timing.SentUtc = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool ok = Client.SendAsync(u).GetAwaiter().GetResult();
        Log.Write(ok ? "sent" : "send.failed", new { mappingId = s.MappingId, version = u.Version, sendMs = sw.Elapsed.TotalMilliseconds, force });
        if (ok) s.LastSentHash = u.Hash;
    }

    private void OnPipeConnection(bool connected)
    {
        foreach (var s in _states.Values.ToList())
        {
            s.LastSentHash = null;
            // Revit (re)started: it may have missed edits or lost its state. Send the newest unless it's acked.
            if (connected && s.Latest is { } u && u.Hash != s.LastAckedHash)
                Chain(s, () => Send(s, u, force: false));
        }
        _uiMessages.Enqueue(connected ? "LiveSync: connected to Revit." : "LiveSync: Revit disconnected; will reconnect.");
    }

    private void OnPipeMessage(Message m)
    {
        switch (m)
        {
            case Ack a:
                OnAck(a);
                break;
            case ResyncRequest r:
                var targets = _states.Values.Where(s => r.MappingId is null || string.Equals(s.MappingId, r.MappingId, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var s in targets) s.ForceRequested = true;
                Log.Write("resync.request", new { mappingId = r.MappingId, mappings = targets.Count });
                break;
            case Ping:
                break;
            default:
                Log.Write("pipe.unexpected", new { type = m.GetType().Name });
                break;
        }
    }

    private void OnAck(Ack a)
    {
        var s = _states.Values.FirstOrDefault(x => string.Equals(x.MappingId, a.MappingId, StringComparison.OrdinalIgnoreCase));
        var t = a.Timing;
        double? editToCommitMs = Ms(t.CommittedUtc - t.LastEditUtc);
        double? sentToCommitMs = Ms(t.CommittedUtc - t.SentUtc);
        double? revitApplyMs = Ms(t.CommittedUtc - t.EventStartUtc);
        Log.Write("ack", new
        {
            mappingId = a.MappingId,
            version = a.Version,
            status = a.Status,
            detail = a.Detail,
            pointsApplied = a.PointsApplied,
            pointsDropped = a.PointsDropped,
            editToCommitMs,
            sentToCommitMs,
            revitWaitMs = Ms(t.EventStartUtc - t.ReceivedUtc),
            revitApplyMs,
            pipeMs = Ms(t.ReceivedUtc - t.SentUtc),
        });
        if (s is null) return;
        s.LastAck = a;

        switch (a.Status)
        {
            case AckStatus.Applied:
                s.LastAckedHash = a.Hash;
                string when = editToCommitMs is { } e ? $"{e / 1000:0.00} s after edit" : $"{sentToCommitMs / 1000:0.00} s after send";
                _uiMessages.Enqueue($"LiveSync [{a.MappingId}] v{a.Version}: applied in Revit {when} ({a.PointsApplied} pts; Revit apply {revitApplyMs:0} ms)."
                                    + (a.Detail is null ? "" : " " + a.Detail));
                break;
            case AckStatus.SkippedSameHash:
                s.LastAckedHash = a.Hash;
                _uiMessages.Enqueue($"LiveSync [{a.MappingId}] v{a.Version}: Revit already up to date.");
                break;
            case AckStatus.Stale:
                break; // a newer version is on its way; its ack reports the result
            default:
                if (s.LastSentHash == a.Hash) s.LastSentHash = null; // let the next edit (or LSSYNC) resend
                _uiMessages.Enqueue($"LiveSync [{a.MappingId}] v{a.Version}: Revit {a.Status}: {a.Detail}");
                break;
        }
    }

    private static double? Ms(TimeSpan? t) => t is { } v ? Math.Round(v.TotalMilliseconds, 1) : null;

    private void OnDocumentCreated(object sender, DocumentCollectionEventArgs e) => Attach(e.Document);
    private void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e) => Detach(e.Document);

    // --- helpers ---

    internal static bool TryParseHandle(string? hex, out Handle handle)
    {
        handle = default;
        if (string.IsNullOrWhiteSpace(hex) || !long.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        handle = new Handle(v);
        return true;
    }

    internal static bool IsLiveTinSurface(ObjectId id) =>
        id.IsValid && !id.IsErased && id.ObjectClass.IsDerivedFrom(RXObject.GetClass(typeof(TinSurface)));

    internal static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (System.Exception) { return false; }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        try
        {
            var dm = AcApp.DocumentManager;
            dm.DocumentCreated -= OnDocumentCreated;
            dm.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
        }
        catch (System.Exception) { /* shutting down */ }
        foreach (var doc in _watchedDocs.Keys.ToList()) Detach(doc);
        Client?.Dispose();
        try { Task.WaitAll(_states.Values.Select(s => s.Pipeline).ToArray(), TimeSpan.FromSeconds(2)); }
        catch (System.Exception) { /* logged inside Prepare */ }
        Log.Write("stop", new { sessionId = SessionId });
        Log.Dispose();
    }
}
