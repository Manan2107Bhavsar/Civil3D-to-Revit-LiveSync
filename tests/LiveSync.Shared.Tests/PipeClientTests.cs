using LiveSync.Shared;

namespace LiveSync.Shared.Tests;

public class PipeClientTests
{
    private static string UniquePipe() => "livesync-test-" + Guid.NewGuid().ToString("N");

    private static async Task Until(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task ClientSends_ServerAcks_ClientReceives()
    {
        var pipe = UniquePipe();
        using var server = new PipeServer(pipe, r =>
        {
            var u = (SurfaceUpdate)r.Message;
            _ = r.Connection.SendAsync(new Ack { MappingId = u.MappingId, Version = u.Version, Status = AckStatus.Applied });
        });
        await server.StartAsync();

        var acks = new TaskCompletionSource<Ack>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new PipeClient(pipe, m => { if (m is Ack a) acks.TrySetResult(a); });
        client.Start();
        await Until(() => client.IsConnected);

        Assert.True(await client.SendAsync(new SurfaceUpdate { MappingId = "eg", Version = 42 }));
        var ack = await acks.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(42, ack.Version);
        Assert.Equal("connected", client.State);
    }

    [Fact]
    public async Task SendWithoutServer_ReturnsFalse_ThenConnectsWhenServerStarts()
    {
        var pipe = UniquePipe();
        int connects = 0;
        using var client = new PipeClient(pipe, _ => { }, onConnectionChanged: up => { if (up) Interlocked.Increment(ref connects); });
        client.Start();
        Assert.False(await client.SendAsync(new Ping()));
        Assert.Equal("connecting", client.State);

        using var server = new PipeServer(pipe, _ => { });
        await server.StartAsync();
        await Until(() => client.IsConnected);
        Assert.Equal(1, connects);
    }

    [Fact]
    public async Task ServerRestart_ClientReconnects_AndServerCanBroadcast()
    {
        var pipe = UniquePipe();
        var resyncs = 0;
        using var client = new PipeClient(pipe, m => { if (m is ResyncRequest) Interlocked.Increment(ref resyncs); });
        client.Start();

        var first = new PipeServer(pipe, _ => { });
        await first.StartAsync();
        await Until(() => client.IsConnected);
        first.Dispose();
        await Until(() => !client.IsConnected);

        using var second = new PipeServer(pipe, _ => { });
        await second.StartAsync();
        await Until(() => second.ClientCount == 1);
        Assert.Equal(1, await second.BroadcastAsync(new ResyncRequest()));
        await Until(() => resyncs == 1);
    }
}
