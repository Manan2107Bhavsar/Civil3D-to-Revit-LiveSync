using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(LiveSync.Civil3d.Plugin))]
[assembly: CommandClass(typeof(LiveSync.Civil3d.Commands))]

namespace LiveSync.Civil3d;

/// <summary>Entry point. Initialize runs on the AutoCAD main thread when the DLL is NETLOADed.</summary>
public sealed class Plugin : IExtensionApplication
{
    public void Initialize()
    {
        try
        {
            SyncController.Instance = new SyncController();
            SyncController.Instance.Start();
            Ui.Write($"LiveSync loaded ({typeof(Plugin).Assembly.Location}). Commands: LSLINK LSSYNC LSUNLINK LSSTATUS LSRELOAD LSDEBUG");
        }
        catch (System.Exception ex)
        {
            Ui.Write($"LiveSync failed to start: {ex}");
        }
    }

    public void Terminate()
    {
        SyncController.Instance?.Dispose();
        SyncController.Instance = null;
    }
}

internal static class Ui
{
    /// <summary>Writes to the active drawing's command line. Main thread only.</summary>
    public static void Write(string message) =>
        AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n" + message + "\n");
}
