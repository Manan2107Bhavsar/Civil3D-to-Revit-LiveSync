using System.IO.Pipes;
using LiveSync.Shared;

namespace LiveSync.Shared.Tests;

public class LatestWinsMailboxTests
{
    private static LatestWinsMailbox<SurfaceUpdate> New() => new(u => u.Version);
    private static SurfaceUpdate V(long v) => new() { MappingId = "eg", Version = v };

    [Fact]
    public void NewerReplacesOlder_OlderIsDropped()
    {
        var box = New();
        Assert.Equal(LatestWinsMailbox<SurfaceUpdate>.PostResult.Added, box.Post("eg", V(12), out _));
        Assert.Equal(LatestWinsMailbox<SurfaceUpdate>.PostResult.Replaced, box.Post("eg", V(13), out var d1));
        Assert.Equal(12, d1!.Version);
        Assert.Equal(LatestWinsMailbox<SurfaceUpdate>.PostResult.Stale, box.Post("EG", V(11), out var d2));
        Assert.Equal(11, d2!.Version);

        Assert.True(box.TryTake("eg", out var taken));
        Assert.Equal(13, taken.Version);
        Assert.False(box.TryTake("eg", out _));
    }

    [Fact]
    public void KeysAreIndependent()
    {
        var box = New();
        box.Post("a", V(1), out _);
        box.Post("b", V(1), out _);
        Assert.Equal(2, box.Count);
        Assert.Equal(2, box.TakeAll().Count);
        Assert.Equal(0, box.Count);
    }
}

public class PipeServerTests
{
    private static string UniquePipe() => "livesync-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task ClientSendsSurface_ServerReceives_AndAcksBack()
    {
        var pipe = UniquePipe();
        var got = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new PipeServer(pipe, r =>
        {
            got.TrySetResult(r);
            var u = (SurfaceUpdate)r.Message;
            _ = r.Connection.SendAsync(new Ack { MappingId = u.MappingId, Version = u.Version, Status = AckStatus.Received });
        });
        await server.StartAsync();
        Assert.Equal("listening", server.State);

        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);

        // Big enough to need many pipe reads (the MCP socket's 8 KB single read would split it).
        var update = new SurfaceUpdate { MappingId = "eg", Version = 7, Vertices = new double[300_000] };
        await Framing.WriteMessageAsync(client, update);

        var received = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var ru = Assert.IsType<SurfaceUpdate>(received.Message);
        Assert.Equal(300_000, ru.Vertices.Length);
        Assert.True(received.Bytes > 8192);

        var ack = Assert.IsType<Ack>(await Framing.ReadMessageAsync(client).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(7, ack.Version);
        Assert.Equal(AckStatus.Received, ack.Status);
        Assert.Equal(1, server.MessagesReceived);
    }

    [Fact]
    public async Task SecondServerOnSameName_ReportsBusy()
    {
        var pipe = UniquePipe();
        using var first = new PipeServer(pipe, _ => { });
        await first.StartAsync();
        using var second = new PipeServer(pipe, _ => { });
        await second.StartAsync();

        Assert.Equal("listening", first.State);
        Assert.Equal("busy", second.State);
    }

    [Fact]
    public async Task BadJson_GetsErrorReply_ConnectionStaysUp()
    {
        var pipe = UniquePipe();
        var got = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new PipeServer(pipe, r => got.TrySetResult(r));
        await server.StartAsync();

        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);
        await Framing.WriteFrameAsync(client, "{not json"u8.ToArray());
        Assert.IsType<ErrorMessage>(await Framing.ReadMessageAsync(client).WaitAsync(TimeSpan.FromSeconds(5)));

        await Framing.WriteMessageAsync(client, new Ping());
        Assert.IsType<Ping>((await got.Task.WaitAsync(TimeSpan.FromSeconds(5))).Message);
    }

    [Fact]
    public async Task Dispose_DisconnectsClients()
    {
        var pipe = UniquePipe();
        var server = new PipeServer(pipe, _ => { });
        await server.StartAsync();
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);

        server.Dispose();
        Assert.Equal("stopped", server.State);
        Assert.Null(await Framing.ReadMessageAsync(client).WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
