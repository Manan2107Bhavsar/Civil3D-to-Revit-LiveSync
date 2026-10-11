using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace LiveSync.Shared;

/// <summary>A decoded message plus where and when it arrived.</summary>
public sealed record ReceivedMessage(Message Message, PipeConnection Connection, DateTime ReceivedUtc, int Bytes, double DecodeMs);

/// <summary>
/// Named-pipe server (Revit side). Accepts any number of clients (Civil 3D now; Inventor and
/// SolidWorks later), reads length-prefixed messages on background threads and hands each one
/// to <c>onMessage</c>. It never touches host (Revit) objects: <c>onMessage</c> runs on a pool
/// thread and must only queue work.
/// </summary>
public sealed class PipeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly JsonlLog? _log;
    private readonly Action<ReceivedMessage> _onMessage;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<PipeConnection> _clients = [];
    private Task? _acceptLoop;
    private int _nextClientId;

    public PipeServer(string pipeName, Action<ReceivedMessage> onMessage, JsonlLog? log = null)
    {
        _pipeName = pipeName;
        _onMessage = onMessage;
        _log = log;
    }

    public string PipeName => _pipeName;

    /// <summary>"starting", "listening", "busy" (another process owns the pipe), "stopped".</summary>
    public string State { get; private set; } = "starting";

    public long MessagesReceived => Interlocked.Read(ref _messages);
    private long _messages;

    public string? LastError { get; private set; }

    public int ClientCount
    {
        get { lock (_clients) return _clients.Count; }
    }

    /// <summary>Completes once the first pipe instance exists (or creating it failed), so a client can connect right after.</summary>
    public Task StartAsync()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(ready, _cts.Token));
        return ready.Task;
    }

    private async Task AcceptLoopAsync(TaskCompletionSource ready, CancellationToken ct)
    {
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                // CurrentUserOnly: the pipe's ACL admits only this Windows user (no LAN, no other users).
                // FirstPipeInstance on the first one: if a second Revit is open, it must not silently
                // share the name, or Civil 3D would connect to whichever instance answers first.
                var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
                              | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options);
            }
            catch (Exception ex) when (first)
            {
                State = "busy";
                LastError = ex.Message;
                _log?.Write("pipe.busy", new { pipe = LiveSyncPaths.PipePath(_pipeName), error = ex.Message });
                ready.TrySetResult();
                return;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _log?.Write("pipe.error", new { where = "create", error = ex.Message });
                if (!await DelayOrStop(1000, ct)) return;
                continue;
            }

            if (first)
            {
                first = false;
                State = "listening";
                _log?.Write("pipe.listening", new { pipe = LiveSyncPaths.PipePath(_pipeName) });
                ready.TrySetResult();
            }

            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                server.Dispose();
                return;
            }
            catch (Exception ex)
            {
                server.Dispose();
                LastError = ex.Message;
                _log?.Write("pipe.error", new { where = "accept", error = ex.Message });
                if (!await DelayOrStop(500, ct)) return;
                continue;
            }

            var conn = new PipeConnection(Interlocked.Increment(ref _nextClientId), server);
            lock (_clients) _clients.Add(conn);
            _ = Task.Run(() => ClientLoopAsync(conn, ct));
        }
    }

    private async Task ClientLoopAsync(PipeConnection conn, CancellationToken ct)
    {
        _log?.Write("pipe.connected", new { clientId = conn.Id, clients = ClientCount });
        string reason = "eof";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await Framing.ReadFrameAsync(conn.Stream, ct: ct).ConfigureAwait(false);
                if (frame is null) break;
                var receivedUtc = DateTime.UtcNow;

                var sw = Stopwatch.StartNew();
                Message message;
                try
                {
                    message = MessageCodec.Deserialize(frame);
                }
                catch (JsonException ex)
                {
                    // Bad content, but the framing is intact: report it and keep the connection.
                    _log?.Write("pipe.badMessage", new { clientId = conn.Id, bytes = frame.Length, error = ex.Message });
                    await conn.SendAsync(new ErrorMessage { Detail = "Bad message: " + ex.Message }).ConfigureAwait(false);
                    continue;
                }
                double decodeMs = sw.Elapsed.TotalMilliseconds;

                Interlocked.Increment(ref _messages);
                try
                {
                    _onMessage(new ReceivedMessage(message, conn, receivedUtc, frame.Length, decodeMs));
                }
                catch (Exception ex)
                {
                    _log?.Write("pipe.handlerError", new { clientId = conn.Id, error = ex.ToString() });
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = "stopping";
        }
        catch (Exception ex)
        {
            // Broken framing (truncated or oversized frame) or a dead pipe: drop this client; it reconnects.
            reason = ex.GetType().Name + ": " + ex.Message;
            LastError = ex.Message;
        }
        finally
        {
            lock (_clients) _clients.Remove(conn);
            conn.Dispose();
            _log?.Write("pipe.disconnected", new { clientId = conn.Id, reason, clients = ClientCount });
        }
    }

    /// <summary>Sends to every connected client; returns how many accepted it.</summary>
    public async Task<int> BroadcastAsync(Message message)
    {
        PipeConnection[] clients;
        lock (_clients) clients = [.. _clients];
        var results = await Task.WhenAll(clients.Select(c => c.SendAsync(message))).ConfigureAwait(false);
        return results.Count(ok => ok);
    }

    private static async Task<bool> DelayOrStop(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
    }

    public void Dispose()
    {
        if (State == "stopped") return;
        _cts.Cancel();
        PipeConnection[] clients;
        lock (_clients) clients = [.. _clients];
        foreach (var c in clients) c.Dispose();
        try { _acceptLoop?.Wait(2000); } catch { /* already logged */ }
        State = "stopped";
        _cts.Dispose();
    }
}

/// <summary>One connected client. <see cref="SendAsync"/> may be called from any thread.</summary>
public sealed class PipeConnection : IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    internal PipeConnection(int id, PipeStream stream)
    {
        Id = id;
        Stream = stream;
    }

    public int Id { get; }
    internal PipeStream Stream { get; }

    /// <summary>Returns false if the client has gone; never throws.</summary>
    public async Task<bool> SendAsync(Message message)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Stream.IsConnected) return false;
            await Framing.WriteMessageAsync(Stream, message).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => Stream.Dispose();
}
