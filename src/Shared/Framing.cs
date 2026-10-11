using System.Buffers.Binary;

namespace LiveSync.Shared;

/// <summary>
/// Length-prefixed frames: 4-byte little-endian payload length, then the payload.
/// Pipes and sockets deliver bytes, not messages; a single Read can return part of a
/// frame or parts of two (the bug in the MCP add-in's 8 KB single-read socket).
/// </summary>
public static class Framing
{
    /// <summary>Default upper bound per frame. ~2.5M vertices as JSON; anything bigger is a bug or decimation is needed.</summary>
    public const int DefaultMaxFrameBytes = 256 * 1024 * 1024;

    /// <summary>Writes one frame. Callers sharing a stream across threads must serialise calls themselves.</summary>
    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one frame. Returns null when the stream ends cleanly between frames (peer disconnected).
    /// </summary>
    /// <exception cref="EndOfStreamException">Stream ended in the middle of a frame.</exception>
    /// <exception cref="InvalidDataException">Length is negative or above <paramref name="maxFrameBytes"/>.</exception>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, int maxFrameBytes = DefaultMaxFrameBytes, CancellationToken ct = default)
    {
        var header = new byte[4];
        int got = await ReadAtLeastAsync(stream, header, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < header.Length) throw new EndOfStreamException("Stream ended inside a frame header.");

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > maxFrameBytes)
            throw new InvalidDataException($"Frame length {length} outside 0..{maxFrameBytes}.");

        var payload = new byte[length];
        if (await ReadAtLeastAsync(stream, payload, ct).ConfigureAwait(false) < length)
            throw new EndOfStreamException("Stream ended inside a frame payload.");
        return payload;
    }

    public static Task WriteMessageAsync(Stream stream, Message message, CancellationToken ct = default) =>
        WriteFrameAsync(stream, MessageCodec.Serialize(message), ct);

    /// <summary>Reads and decodes one message; null on clean disconnect.</summary>
    public static async Task<Message?> ReadMessageAsync(Stream stream, int maxFrameBytes = DefaultMaxFrameBytes, CancellationToken ct = default)
    {
        var frame = await ReadFrameAsync(stream, maxFrameBytes, ct).ConfigureAwait(false);
        return frame is null ? null : MessageCodec.Deserialize(frame);
    }

    // Fills buffer completely unless the stream ends; returns bytes read.
    private static async Task<int> ReadAtLeastAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
