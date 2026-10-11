namespace LiveSync.Shared;

public static class LiveSyncPaths
{
    /// <summary>%APPDATA%\C3dRevitLiveSync. Overridable with the LIVESYNC_HOME environment variable (tests, second setups).</summary>
    public static string AppDataDir =>
        Environment.GetEnvironmentVariable("LIVESYNC_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C3dRevitLiveSync");

    public static string ConfigPath => Path.Combine(AppDataDir, "mapping.json");
    public static string DefaultLogDir => Path.Combine(AppDataDir, "logs");

    /// <summary>Full Win32 path for a pipe name, for log messages. .NET pipe APIs take the bare name.</summary>
    public static string PipePath(string pipeName) => @"\\.\pipe\" + pipeName;
}
