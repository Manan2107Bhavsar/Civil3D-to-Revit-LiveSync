using LiveSync.Shared;

namespace LiveSync.Shared.Tests;

public class FramingTests
{
    [Fact]
    public async Task RoundTrip_MultipleFrames_OnOneStream()
    {
        var ms = new MemoryStream();
        await Framing.WriteFrameAsync(ms, "first"u8.ToArray());
        await Framing.WriteFrameAsync(ms, Array.Empty<byte>());
        await Framing.WriteFrameAsync(ms, new byte[100_000]);
        ms.Position = 0;

        Assert.Equal("first"u8.ToArray(), await Framing.ReadFrameAsync(ms));
        Assert.Empty((await Framing.ReadFrameAsync(ms))!);
        Assert.Equal(100_000, (await Framing.ReadFrameAsync(ms))!.Length);
        Assert.Null(await Framing.ReadFrameAsync(ms)); // clean EOF between frames
    }

    [Fact]
    public async Task Read_ReassemblesFramesDeliveredOneByteAtATime()
    {
        // The MCP add-in's single-Read approach would fail this: pipes/sockets may split frames anywhere.
        var ms = new MemoryStream();
        var payload = Enumerable.Range(0, 20_000).Select(i => (byte)i).ToArray();
        await Framing.WriteFrameAsync(ms, payload);
        var trickle = new TrickleStream(ms.ToArray());

        Assert.Equal(payload, await Framing.ReadFrameAsync(trickle));
    }

    [Fact]
    public async Task Read_TruncatedPayload_Throws()
    {
        var ms = new MemoryStream();
        await Framing.WriteFrameAsync(ms, new byte[50]);
        var truncated = new MemoryStream(ms.ToArray()[..30]);

        await Assert.ThrowsAsync<EndOfStreamException>(() => Framing.ReadFrameAsync(truncated));
    }

    [Fact]
    public async Task Read_OversizeLength_ThrowsBeforeAllocating()
    {
        var ms = new MemoryStream();
        await Framing.WriteFrameAsync(ms, new byte[2000]);
        ms.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => Framing.ReadFrameAsync(ms, maxFrameBytes: 1000));
    }

    [Fact]
    public async Task Messages_RoundTripThroughFrames()
    {
        var ms = new MemoryStream();
        await Framing.WriteMessageAsync(ms, new Ping());
        await Framing.WriteMessageAsync(ms, new ResyncRequest { MappingId = "eg" });
        ms.Position = 0;

        Assert.IsType<Ping>(await Framing.ReadMessageAsync(ms));
        Assert.Equal("eg", Assert.IsType<ResyncRequest>(await Framing.ReadMessageAsync(ms)).MappingId);
        Assert.Null(await Framing.ReadMessageAsync(ms));
    }

    /// <summary>Returns at most one byte per Read, like a worst-case pipe.</summary>
    private sealed class TrickleStream(byte[] data) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_pos >= data.Length || buffer.Length == 0) return 0;
            buffer[0] = data[_pos++];
            return 1;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => new(Read(buffer.Span));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
