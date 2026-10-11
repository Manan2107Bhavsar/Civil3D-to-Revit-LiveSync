using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using LiveSync.Shared;

namespace LiveSync.Revit;

/// <summary>
/// Revit entry point. Starts the pipe server and builds the ribbon. Everything that arrives on
/// the pipe is handled by <see cref="Receiver"/> on background threads; the Revit API is only
/// used from ribbon commands now (and from an ExternalEvent from step 4).
/// </summary>
public sealed class App : IExternalApplication
{
    /// <summary>Set in OnStartup; commands reach the running receiver through this.</summary>
    public static Receiver? Current { get; private set; }

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            var config = ConfigStore.Load();
            var log = new JsonlLog("revit", config.ResolvedLogDir);
            var receiver = new Receiver(config, log, application.ControlledApplication.VersionBuild);
            // Created here because ExternalEvent.Create needs an API context; raised from pipe threads.
            receiver.AttachApplyEvent(ExternalEvent.Create(new ApplyHandler(receiver)));
            Current = receiver;
            // Fire and forget: the first pipe instance is created on a pool thread in a few ms.
            // Startup must not wait on it.
            _ = receiver.StartAsync();

            application.ControlledApplication.DocumentChanged += OnDocumentChanged;
            application.ControlledApplication.DocumentOpened += OnDocumentOpened;
            CreateRibbon(application);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("LiveSync", "LiveSync receiver failed to start:\n" + ex.Message);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
        application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
        Current?.Dispose();
        Current = null;
        return Result.Succeeded;
    }

    // After an Undo/Redo the toposolid no longer matches what was last applied, so forget it;
    // otherwise the next payload with that hash would be skipped as "unchanged".
    private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (e.Operation is UndoOperation.TransactionUndone or UndoOperation.TransactionRedone)
            Current?.ForgetApplied("undo/redo");
    }

    private static void OnDocumentOpened(object? sender, DocumentOpenedEventArgs e) =>
        Current?.Unpark("opened " + e.Document.Title);

    private static void CreateRibbon(UIControlledApplication application)
    {
        // Text-only buttons on the Add-Ins tab; icons can come later.
        var panel = application.CreateRibbonPanel("LiveSync");
        string asm = typeof(App).Assembly.Location;
        panel.AddItem(new PushButtonData("LiveSyncLink", "Link\nToposolid", asm, typeof(LinkToposolidCommand).FullName)
        {
            ToolTip = "Select a toposolid to link it to the Civil 3D surface last received, or select nothing to create one from that surface.",
        });
        panel.AddItem(new PushButtonData("LiveSyncForce", "Force\nSync", asm, typeof(ForceSyncCommand).FullName)
        {
            ToolTip = "Ask Civil 3D for a fresh surface and apply it even if unchanged. Without Civil 3D, re-applies the last surface received.",
        });
        panel.AddItem(new PushButtonData("LiveSyncStatus", "Status", asm, typeof(StatusCommand).FullName)
        {
            ToolTip = "Pipe state, connected clients and the last payload received.",
            AvailabilityClassName = typeof(AlwaysAvailable).FullName,
        });
    }
}
