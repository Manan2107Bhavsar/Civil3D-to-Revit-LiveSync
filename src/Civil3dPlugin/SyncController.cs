using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using LiveSync.Shared;
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
}

/// <summary>Raised once per edit burst after the drawing has been quiet for DebounceMs and no command is running.</summary>
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
    public IReadOnlyCollection<MappingState> States => _states.Values;

    /// <summary>Step 2 hooks extraction here.</summary>
    public event Action<ChangeDetected>? Changed;

    public void Start()
    {
        var dm = AcApp.DocumentManager;
        dm.DocumentCreated += OnDocumentCreated;
        dm.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
        _timer.Tick += OnTick;
        _timer.Start();

        Log.Write("start", new { sessionId = SessionId, configPath = LiveSyncPaths.ConfigPath, mappings = Config.Mappings.Count, Config.DebounceMs });
        AttachAll();
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
            Log.Write("event.modified", new { handle = id.Handle.ToString(), cls = id.ObjectClass?.Name, linked = s?.MappingId });
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
        var now = DateTime.UtcNow;
        foreach (var s in _states.Values)
        {
            if (!s.Dirty || s.Doc is null || s.LastEditUtc is null) continue;
            if ((now - s.LastEditUtc.Value).TotalMilliseconds < Config.DebounceMs) continue;
            if (s.SurfaceId.IsErased) { s.Dirty = false; continue; }

            // Don't sync halfway through a grip-drag or an interactive edit command.
            var cmd = s.Doc.CommandInProgress;
            if (!string.IsNullOrEmpty(cmd))
            {
                if (!s.LoggedWaitingForCommand)
                {
                    Log.Write("debounce.wait", new { mappingId = s.MappingId, command = cmd });
                    s.LoggedWaitingForCommand = true;
                }
                continue;
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

            try { Changed?.Invoke(change); }
            catch (System.Exception ex) { Log.Write("error", new { mappingId = s.MappingId, where = "Changed", error = ex.ToString() }); }
        }
    }

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
        Log.Write("stop", new { sessionId = SessionId });
        Log.Dispose();
    }
}
