using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiveSync.Shared;

/// <summary>Message ⇄ UTF-8 JSON. Framing is separate (<see cref="Framing"/>).</summary>
public static class MessageCodec
{
    public const int SchemaVersion = 1;

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Lets "type" appear anywhere in the object, not only first (hand-written test payloads).
        AllowOutOfOrderMetadataProperties = true,
    };

    public static byte[] Serialize(Message message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, Options);

    /// <exception cref="JsonException">Malformed JSON, unknown "type", or a newer schema.</exception>
    public static Message Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        var message = JsonSerializer.Deserialize<Message>(utf8Json, Options)
                      ?? throw new JsonException("Message was null.");
        if (message.Schema > SchemaVersion)
            throw new JsonException($"Message schema {message.Schema} is newer than supported ({SchemaVersion}).");
        return message;
    }
}
