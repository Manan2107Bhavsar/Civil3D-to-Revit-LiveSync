using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LiveSync.Shared;

namespace LiveSync.Revit;

/// <summary>
/// Runs on Revit's API thread when the ExternalEvent fires (Revit idle, no command running).
/// Drains the mailbox: for each mapping, applies only the newest payload, then acks.
/// </summary>
public sealed class ApplyHandler(Receiver rx) : IExternalEventHandler
{
    public string GetName() => "LiveSync apply";

    public void Execute(UIApplication app)
    {
        foreach (var (_, p) in rx.Mailbox.TakeAll())
        {
            try
            {
                ApplyOne(app, p);
            }
            catch (Exception ex)
            {
                rx.Log.Write("apply.error", new { mappingId = p.Update.MappingId, version = p.Update.Version, error = ex.ToString() });
                Reply(p, AckStatus.Error, ex.Message);
            }
        }
    }

    private void ApplyOne(UIApplication app, Pending p)
    {
        var u = p.Update;
        u.Timing.EventStartUtc = DateTime.UtcNow;

        var mapping = rx.Config.Find(u.MappingId);
        if (mapping?.Revit.ToposolidUniqueId is null)
        {
            rx.Log.Write("apply.unlinked", new { mappingId = u.MappingId, version = u.Version });
            Reply(p, AckStatus.Error, $"mapping '{u.MappingId}' has no linked toposolid; in Revit use LiveSync > Link Toposolid");
            return;
        }

        var (doc, topo) = Resolve(app, mapping.Revit);
        if (doc is null || topo is null)
        {
            rx.Log.Write("apply.notFound", new { mappingId = u.MappingId, version = u.Version, mapping.Revit.DocumentPath, mapping.Revit.ToposolidUniqueId });
            // Keep the newest one; it is retried when a project opens.
            rx.Parked.AddOrUpdate(u.MappingId, p, (_, old) => p.Update.Version >= old.Update.Version ? p : old);
            Reply(p, AckStatus.Error, "linked toposolid not found in any open project; will apply when the project is opened");
            return;
        }

        // A parked payload can be older than what was applied since (e.g. after Link Toposolid).
        if (!p.Force && rx.Applied.TryGetValue(u.MappingId, out var newer) && u.Version < newer.Version)
        {
            rx.Log.Write("apply.stale", new { mappingId = u.MappingId, version = u.Version, appliedVersion = newer.Version });
            Reply(p, AckStatus.Stale, $"v{newer.Version} already applied");
            return;
        }

        if (!p.Force && rx.Applied.TryGetValue(u.MappingId, out var last) && last.Hash == u.Hash)
        {
            rx.Applied[u.MappingId] = last with { Version = u.Version };
            rx.Log.Write("apply.skipped", new { mappingId = u.MappingId, version = u.Version, reason = "same hash" });
            Reply(p, AckStatus.SkippedSameHash, null);
            return;
        }

        var frame = CoordinateFrame.For(doc, u.Units);
        var result = ToposolidUpdater.Apply(doc, topo, u, frame);
        u.Timing.CommittedUtc = DateTime.UtcNow;

        rx.Log.Write(result.Ok ? "applied" : "apply.failed", new
        {
            mappingId = u.MappingId,
            version = u.Version,
            elementId = topo.Id.Value,
            doc = doc.Title,
            result.Error,
            result.PointsSent,
            result.PointsApplied,
            result.Outside,
            result.NearEdge,
            result.Duplicate,
            result.OnCorner,
            result.Corners,
            result.CornersSet,
            flatTopFt = R(result.FlatTopFt),
            thicknessFt = R(result.ThicknessFt),
            minTargetFt = R(result.MinTargetFt),
            maxTargetFt = R(result.MaxTargetFt),
            baseLoweredFt = R(result.BaseLoweredFt),
            result.FirstAddError,
            addZDeltaMaxMm = Math.Round(result.AddZDeltaMaxFt * 304.8, 3),
            cornerZDeltaMaxMm = Math.Round(result.CornerZDeltaMaxFt * 304.8, 3),
            result.ResetMs,
            result.AddMs,
            result.CornersMs,
            result.CommitMs,
            // Local re-applies (Link, Force Sync without Civil) reuse an old payload: its receive time isn't meaningful.
            waitMs = p.Connection is null ? null : Ms(u.Timing.EventStartUtc - u.Timing.ReceivedUtc),
            local = p.Connection is null,
            applyMs = Ms(u.Timing.CommittedUtc - u.Timing.EventStartUtc),
            sentToCommitMs = Ms(u.Timing.CommittedUtc - u.Timing.SentUtc),
            editToCommitMs = Ms(u.Timing.CommittedUtc - u.Timing.LastEditUtc),
            frameInverted = frame.UsedInverse,
            sharedOfOriginFt = new[] { frame.SharedOfInternalOrigin.X, frame.SharedOfInternalOrigin.Y, frame.SharedOfInternalOrigin.Z },
            warnings = result.Warnings.Take(10),
        });

        if (result.Ok)
        {
            rx.Applied[u.MappingId] = new AppliedState(u.Version, u.Hash, u.Timing.CommittedUtc!.Value, result);
            if (rx.Parked.TryGetValue(u.MappingId, out var parked) && parked.Update.Version <= u.Version)
                rx.Parked.TryRemove(u.MappingId, out _);
            Reply(p, AckStatus.Applied, result.Outside + result.NearEdge > 0
                ? $"{result.Outside} outside + {result.NearEdge} on the edge not added" : null, result);
        }
        else
        {
            Reply(p, AckStatus.Error, result.Error, result);
        }
    }

    /// <summary>Finds the toposolid by UniqueId (then ElementId) in the mapped project, else in any open project.</summary>
    public static (Document? Doc, Toposolid? Topo) Resolve(UIApplication app, RevitSide side)
    {
        var docs = app.Application.Documents.Cast<Document>().Where(d => !d.IsLinked && !d.IsFamilyDocument).ToList();
        var ordered = docs.OrderByDescending(d => !string.IsNullOrEmpty(side.DocumentPath)
                                                  && string.Equals(d.PathName, side.DocumentPath, StringComparison.OrdinalIgnoreCase));
        foreach (var d in ordered)
        {
            if (side.ToposolidUniqueId is { } uid && d.GetElement(uid) is Toposolid t) return (d, t);
        }
        // ElementId only inside the mapped project: ids aren't unique across projects.
        var mapped = docs.FirstOrDefault(d => string.Equals(d.PathName, side.DocumentPath, StringComparison.OrdinalIgnoreCase));
        if (mapped is not null && side.ElementId is { } id && mapped.GetElement(new ElementId(id)) is Toposolid byId) return (mapped, byId);
        return (null, null);
    }

    private static void Reply(Pending p, string status, string? detail, ApplyResult? result = null) =>
        _ = p.Connection?.SendAsync(Receiver.MakeAck(p.Update, status, detail, result));

    private static double? R(double? v) => v is { } d ? Math.Round(d, 3) : null;
    private static double? Ms(TimeSpan? t) => t is { } v ? Math.Round(v.TotalMilliseconds, 1) : null;
}
