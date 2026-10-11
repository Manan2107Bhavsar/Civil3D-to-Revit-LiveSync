using System.Text;
using System.Text.Json;
using LiveSync.Shared;

namespace LiveSync.Shared.Tests;

public class MessageCodecTests
{
    [Fact]
    public void SurfaceUpdate_RoundTrips_AllFields()
    {
        var msg = new SurfaceUpdate
        {
            MappingId = "eg-site",
            Source = new SourceInfo { App = "Civil3D 2027", Dwg = @"C:\p\Site.dwg", Handle = "2A7F", Name = "EG" },
            Version = 14,
            SessionId = "s1",
            Hash = "sha256:abc",
            Units = Units.Metres,
            Vertices = [383000.5, 5776000.25, 512.125, 1, 2, 3, 4, 5, 6],
            Triangles = [0, 1, 2],
            ExtraPoints = [7, 8, 9],
            Boundary = [0, 0, 1, 0, 1, 1],
            Timing = new Timing { LastEditUtc = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc) },
        };

        var back = Assert.IsType<SurfaceUpdate>(MessageCodec.Deserialize(MessageCodec.Serialize(msg)));

        Assert.Equal(msg.MappingId, back.MappingId);
        Assert.Equal("2A7F", back.Source.Handle);
        Assert.Equal(14, back.Version);
        Assert.Equal(msg.Vertices, back.Vertices); // doubles must survive exactly (state-plane magnitudes)
        Assert.Equal(msg.Triangles, back.Triangles);
        Assert.Equal(msg.ExtraPoints, back.ExtraPoints);
        Assert.Equal(msg.Boundary, back.Boundary);
        Assert.Equal(msg.Timing.LastEditUtc, back.Timing.LastEditUtc);
    }

    [Fact]
    public void Serialize_WritesTypeDiscriminator_AndCamelCase()
    {
        var json = Encoding.UTF8.GetString(MessageCodec.Serialize(new Ack { MappingId = "eg", Version = 3 }));
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("ack", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("eg", doc.RootElement.GetProperty("mappingId").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("schema").GetInt32());
    }

    [Fact]
    public void Deserialize_AcceptsTypeNotFirst()
    {
        var json = """{"mappingId":"eg","schema":1,"type":"resync.request"}"""u8;
        Assert.IsType<ResyncRequest>(MessageCodec.Deserialize(json));
    }

    [Fact]
    public void Deserialize_UnknownType_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => MessageCodec.Deserialize("""{"type":"corridor.update","schema":1}"""u8));
    }

    [Fact]
    public void Deserialize_NewerSchema_Throws()
    {
        Assert.ThrowsAny<JsonException>(() => MessageCodec.Deserialize("""{"type":"ping","schema":99}"""u8));
    }
}
