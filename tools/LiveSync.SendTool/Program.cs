using System.Diagnostics;
using System.IO.Pipes;
using LiveSync.Shared;

// Usage:
//   dotnet run --project tools/LiveSync.SendTool -- [payload.json] [options]
// Options:
//   --repeat N       send N times (default 1)
//   --interval MS    wait between sends (default 0 = back to back, to exercise latest-wins)
//   --bump           give each send a new, higher version (otherwise repeats are "stale" in Revit)
//   --synthetic N    ignore the file; send an N x N grid surface (load test, e.g. 200 = 40,000 pts)
//   --pipe NAME      pipe name (default: mapping.json pipeName)
//   --ping           send a ping instead of a payload
//   --dz D           raise every point by D (payload units) and give it a new version; repeats raise by D each time
// Default payload: %APPDATA%\C3dRevitLiveSync\payloads\eg.json (written by Civil 3D LSSYNC).

string? file = null;
int repeat = 1, intervalMs = 0, synthetic = 0;
bool bump = false, ping = false;
double dz = 0;
string pipe = ConfigStore.Load().PipeName;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--repeat": repeat = int.Parse(args[++i]); break;
        case "--interval": intervalMs = int.Parse(args[++i]); break;
        case "--bump": bump = true; break;
        case "--synthetic": synthetic = int.Parse(args[++i]); break;
        case "--pipe": pipe = args[++i]; break;
        case "--ping": ping = true; break;
        case "--dz": dz = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); bump = true; break;
        case "-h" or "--help":
            Console.WriteLine("LiveSync.SendTool [payload.json] [--repeat N] [--interval MS] [--bump] [--synthetic N] [--pipe NAME] [--ping] [--dz D]");
            return 0;
        default: file = args[i]; break;
    }
}

SurfaceUpdate? template = null;
if (!ping)
{
    if (synthetic > 0)
    {
        template = Synthetic(synthetic);
        Console.WriteLine($"Synthetic {synthetic}x{synthetic} grid: {template.Vertices.Length / 3} vertices");
    }
    else
    {
        file ??= Path.Combine(LiveSyncPaths.AppDataDir, "payloads", "eg.json");
        if (!File.Exists(file)) { Console.Error.WriteLine($"Payload not found: {file}"); return 2; }
        if (MessageCodec.Deserialize(File.ReadAllBytes(file)) is not SurfaceUpdate u)
        {
            Console.Error.WriteLine($"{file} is not a surface.update message.");
            return 2;
        }
        template = u;
        Console.WriteLine($"Payload {file}: {u.MappingId} v{u.Version}, {u.Vertices.Length / 3} vertices + {u.ExtraPoints.Length / 3} extra, {u.Units}");
    }
}

using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
Console.WriteLine($"Connecting to {LiveSyncPaths.PipePath(pipe)} ...");
try
{
    await client.ConnectAsync(5000);
}
catch (TimeoutException)
{
    Console.Error.WriteLine("No server within 5 s. Is Revit running with the LiveSync add-in loaded?");
    return 1;
}
Console.WriteLine("Connected.");

// Replies are read on their own task, so back-to-back sends don't wait for each ack.
var sentAt = new Dictionary<long, long>();
int expected = ping ? 1 : repeat, replies = 0;
var allReplies = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var reader = Task.Run(async () =>
{
    try
    {
        while (await Framing.ReadMessageAsync(client) is { } m)
        {
            long now = Stopwatch.GetTimestamp();
            switch (m)
            {
                case Ack a:
                    long t0;
                    lock (sentAt) sentAt.TryGetValue(a.Version, out t0);
                    Console.WriteLine($"  ack v{a.Version}: {a.Status}" + (a.Detail is null ? "" : $" ({a.Detail})")
                                      + (a.Status == AckStatus.Applied ? $"  {a.PointsApplied} pts applied, {a.PointsDropped} dropped" : "")
                                      + (t0 != 0 ? $"  round trip {Stopwatch.GetElapsedTime(t0, now).TotalMilliseconds:F1} ms" : ""));
                    break;
                case Ping:
                    Console.WriteLine("  pong");
                    break;
                case ErrorMessage e:
                    Console.WriteLine($"  error: {e.Detail}");
                    break;
                default:
                    Console.WriteLine($"  {m.GetType().Name}");
                    break;
            }
            if (Interlocked.Increment(ref replies) >= expected) allReplies.TrySetResult();
        }
        if (!allReplies.Task.IsCompleted) Console.WriteLine("Server closed the connection.");
    }
    catch (Exception ex)
    {
        Console.WriteLine("Read failed: " + ex.Message);
    }
    allReplies.TrySetResult();
});

if (ping)
{
    await Framing.WriteMessageAsync(client, new Ping());
}
else
{
    long baseVersion = Math.Max(template!.Version, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    for (int n = 0; n < repeat; n++)
    {
        var u = template;
        u.Version = bump ? baseVersion + n : template.Version;
        if (dz != 0)
        {
            for (int k = 2; k < u.Vertices.Length; k += 3) u.Vertices[k] += dz;
            for (int k = 2; k < u.ExtraPoints.Length; k += 3) u.ExtraPoints[k] += dz;
            u.Hash = SurfaceHasher.Compute(u);
        }
        u.Timing.SentUtc = DateTime.UtcNow;
        var bytes = MessageCodec.Serialize(u);
        lock (sentAt) sentAt[u.Version] = Stopwatch.GetTimestamp();
        var sw = Stopwatch.StartNew();
        await Framing.WriteFrameAsync(client, bytes);
        Console.WriteLine($"Sent v{u.Version}: {bytes.Length / 1024.0:F1} KB in {sw.Elapsed.TotalMilliseconds:F1} ms");
        if (intervalMs > 0 && n < repeat - 1) await Task.Delay(intervalMs);
    }
}

if (await Task.WhenAny(allReplies.Task, Task.Delay(30_000)) != allReplies.Task)
{
    Console.Error.WriteLine($"Timed out: {replies}/{expected} replies after 30 s.");
    return 1;
}
Console.WriteLine($"Done: {replies}/{expected} replies.");
return replies >= expected ? 0 : 1;

// A gently rolling n x n grid at 1 m spacing near the test site's coordinates.
static SurfaceUpdate Synthetic(int n)
{
    var v = new double[n * n * 3];
    for (int r = 0, k = 0; r < n; r++)
        for (int c = 0; c < n; c++)
        {
            v[k++] = 383000 + c;
            v[k++] = 5776000 + r;
            v[k++] = 500 + 2 * Math.Sin(c / 15.0) * Math.Cos(r / 20.0);
        }
    var tri = new List<int>((n - 1) * (n - 1) * 6);
    for (int r = 0; r < n - 1; r++)
        for (int c = 0; c < n - 1; c++)
        {
            int a = r * n + c, b = a + 1, d = a + n, e = d + 1;
            tri.AddRange([a, b, e, a, e, d]);
        }
    var u = new SurfaceUpdate
    {
        MappingId = "synthetic",
        Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Source = new SourceInfo { App = "SendTool", Name = $"grid{n}" },
        SessionId = "sendtool",
        Units = Units.Metres,
        Vertices = v,
        Triangles = [.. tri],
        Boundary = [383000, 5776000, 383000 + n - 1, 5776000, 383000 + n - 1, 5776000 + n - 1, 383000, 5776000 + n - 1],
    };
    u.Hash = SurfaceHasher.Compute(u);
    return u;
}
