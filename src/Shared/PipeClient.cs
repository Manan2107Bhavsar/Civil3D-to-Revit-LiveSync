using System.IO.Pipes;

namespace LiveSync.Shared;

/// <summary>
/// Named-pipe client (Civil 3D side). Keeps trying to connect every <see cref="RetryMs"/> while
/// Revit isn't running, and reconnects when Revit closes and reopens. Reads incoming messages
/// (acks, resync requests) on a background thread and hands them to <c>onMessage</c>, which
/// must not touch host objects. Only state changes are logged, not every failed retry.
/// </summary>
public sealed class PipeClient : IDisposable
{
    public const int RetryMs = 2000;
    private const int ConnectTimeoutMs = 1000;

    private readonly string _pipeName;
    private readonly Action<Message> _onMessage;
    private readonly Action<bool>? _onConnectionChanged;
    private readonly JsonlLog? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile NamedPipeClientStream? _stream;
    private Task? _loop;

    /// <param name="onConnectionChanged">true after connecting, false after losing the connection. Pool thread.</param>
    public PipeClient(string pipeName, Action<Message> onMessage, JsonlLog? log = null, Action<bool>? onConnectionChanged = null)
    {
        _pipeName = pipeName;
        _onMessage = onMessage;
        _log = log;
        _onConnectionChanged = onConnectionChanged;
    }

    public string PipeName => _pipeName;

    /// <summary>"stopped" until Start, then "connecting" / "connected", and "stopped" after Dispose.</summary>
    public string State { get; private set; } = "stopped";

    public bool IsConnected => _stream is { IsConnected: true };
    public DateTime? ConnectedSinceUtc { get; private set; }

    public void Start()
    {
        if (_loop is not null) return;
        State = "connecting";
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        bool loggedWaiting = false;
        while (!ct.IsCancellationRequested)
        {
            var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await client.ConnectAsync(ConnectTimeoutMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                return;
            }
            catch (Exception ex)
            {
                client.Dispose();
                // TimeoutException = no server (Revit closed); others (e.g. UnauthorizedAccess) are worth logging too.
                if (!loggedWaiting)
                {
                    _log?.Write("pipe.waiting", new { pipe = LiveSyncPaths.PipePath(_pipeName), reason = ex.GetType().Name });
                    loggedWaiting = true;
                }
                if (!await DelayOrStop(RetryMs, ct)) return;
                continue;
            }

            loggedWaiting = false;
            _stream = client;
            State = "connected";
            ConnectedSinceUtc = DateTime.UtcNow;
            _log?.Write("pipe.connected", new { pipe = LiveSyncPaths.PipePath(_pipeName) });
            Notify(true);

            string reason = "eof";
            try
            {
                while (await Framing.ReadMessageAsync(client, ct: ct).ConfigureAwait(false) is { } m)
                {
                    try { _onMessage(m); }
                    catch (Exception ex) { _log?.Write("pipe.handlerError", new { error = ex.ToString() }); }
                }
            }
            catch (OperationCanceledException) { reason = "stopping"; }
            catch (Exception ex) { reason = ex.GetType().Name + ": " + ex.Message; }
            finally
            {
                _stream = null;
                client.Dispose();
                ConnectedSinceUtc = null;
                if (!ct.IsCancellationRequested) State = "connecting";
                _log?.Write("pipe.disconnected", new { reason });
                Notify(false);
            }
            if (!await DelayOrStop(RetryMs, ct)) return;
        }
    }

    /// <summary>Sends one message; false if not connected or the write failed. Safe from any thread; never throws.</summary>
    public async Task<bool> SendAsync(Message message)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var s = _stream;
            if (s is null || !s.IsConnected) return false;
            await Framing.WriteMessageAsync(s, message).ConfigureAwait(false);
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

    private void Notify(bool connected)
    {
        try { _onConnectionChanged?.Invoke(connected); }
        catch (Exception ex) { _log?.Write("pipe.handlerError", new { error = ex.ToString() }); }
    }

    private static async Task<bool> DelayOrStop(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
    }

    public void Dispose()
    {
        if (State == "stopped" && _loop is null) return;
        _cts.Cancel();
        _stream?.Dispose();
        try { _loop?.Wait(2000); } catch { /* logged in the loop */ }
        _loop = null;
        State = "stopped";
    }
}
