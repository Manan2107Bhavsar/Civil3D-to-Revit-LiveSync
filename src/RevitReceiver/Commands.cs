using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LiveSync.Shared;

namespace LiveSync.Revit;

/// <summary>Keeps Status enabled with no project open (Revit disables external commands then by default).</summary>
public sealed class AlwaysAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class StatusCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var r = App.Current;
        if (r is null)
        {
            TaskDialog.Show("LiveSync", "The LiveSync receiver is not running (see the Revit journal for the startup error).");
            return Result.Succeeded;
        }

        var s = new StringBuilder();
        s.AppendLine($"Pipe: {LiveSyncPaths.PipePath(r.Server.PipeName)}  [{r.Server.State}]");
        if (r.Server.State == "busy")
            s.AppendLine("Another Revit session owns the pipe. Only one Revit can receive at a time.");
        s.AppendLine($"Clients connected: {r.Server.ClientCount}");
        s.AppendLine($"Messages received: {r.Server.MessagesReceived}   superseded: {r.Superseded}   pending: {r.Mailbox.Count}");
        if (r.Last is { } l)
        {
            s.AppendLine();
            s.AppendLine($"Last payload: {l.MappingId} v{l.Version}");
            s.AppendLine($"  {l.Vertices} vertices + {l.ExtraPoints} breakline points, {l.Bytes / 1024.0:F1} KB");
            s.AppendLine($"  received {l.ReceivedUtc.ToLocalTime():HH:mm:ss.fff}, decode {l.DecodeMs:F1} ms"
                         + (l.TransitMs is { } t ? $", pipe transit {t:F1} ms" : ""));
        }
        else
        {
            s.AppendLine("No payload received yet.");
        }
        foreach (var (id, a) in r.Applied)
        {
            s.AppendLine();
            s.AppendLine($"Applied {id} v{a.Version} at {a.CommittedUtc.ToLocalTime():HH:mm:ss}: {a.Result.PointsApplied} points + {a.Result.CornersSet} corners"
                         + $" (outside {a.Result.Outside}, on edge {a.Result.NearEdge}), transaction {a.Result.AddMs + a.Result.CornersMs + a.Result.CommitMs + a.Result.ResetMs:F0} ms");
        }
        if (r.Server.LastError is { } err) s.AppendLine($"Last pipe error: {err}");
        s.AppendLine();
        s.AppendLine($"Session {r.SessionId}. Mappings in config: {r.Config.Mappings.Count}");
        s.AppendLine($"Log: {r.Log.CurrentFile ?? r.Config.ResolvedLogDir}");

        TaskDialog.Show("LiveSync status", s.ToString());
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.ReadOnly)]
public sealed class ForceSyncCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var rx = App.Current;
        if (rx is null) return Result.Cancelled;

        // Civil 3D connected: ask it to extract and send a fresh payload (applied even if unchanged).
        if (rx.Server.ClientCount > 0)
        {
            int n = rx.Server.BroadcastAsync(new ResyncRequest()).GetAwaiter().GetResult();
            rx.Log.Write("resync.sent", new { clients = n });
            TaskDialog.Show("LiveSync", $"Asked Civil 3D for a fresh surface ({n} client(s)). It applies as soon as Civil 3D is idle.");
            return Result.Succeeded;
        }

        // Otherwise re-apply the last payload received for each linked mapping.
        var linked = rx.LatestPayload.Values.Where(u => rx.Config.Find(u.MappingId)?.Revit.ToposolidUniqueId is not null).ToList();
        foreach (var u in linked) rx.Enqueue(new Pending(u, null, Force: true));
        rx.Log.Write("resync.local", new { mappings = linked.Select(u => u.MappingId) });
        TaskDialog.Show("LiveSync", linked.Count > 0
            ? $"Civil 3D isn't connected. Re-applying the last surface received: {string.Join(", ", linked.Select(u => $"{u.MappingId} v{u.Version}"))}."
            : "Civil 3D isn't connected and no surface has been received in this Revit session.");
        return Result.Succeeded;
    }
}
