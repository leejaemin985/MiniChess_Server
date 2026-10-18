using System.Text.Json;
using System.Text.Json.Serialization;
using MiniChess.Core.Common;
using MiniChess.Core.Events;
using MiniChess.Protocol;

namespace MiniChess.Server.Protocol;

/// <summary>
/// 메시지 JSON 형식: { "Type": "Move", "Payload": { ... } }. Type 은 MessageTypes 이름이다.
/// enum 은 이름 문자열, Position 은 { "X": 0, "Y": 0 } 이다. 클라이언트(Unity)도 같은 형식을 쓴다.
/// </summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new PositionConverter(), new JsonStringEnumConverter() },
    };

    public static string Encode(ServerMessage message)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("Type", MessageTypes.NameOf(message.GetType()));
            writer.WritePropertyName("Payload");
            JsonSerializer.Serialize(writer, message, message.GetType(), Options);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>클라이언트 메시지를 읽는다. 모르는 종류이거나 형식이 잘못되었으면 null.</summary>
    public static ClientMessage? DecodeClient(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("Type", out JsonElement typeName) || !root.TryGetProperty("Payload", out JsonElement payload))
                return null;

            Type? type = MessageTypes.Find(typeName.GetString());
            if (type == null || !typeof(ClientMessage).IsAssignableFrom(type))
                return null;

            return (ClientMessage?)payload.Deserialize(type, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>이벤트를 봉투로 감싼다. 본문은 이벤트의 실제 클래스로 직렬화된다.</summary>
    public static EventEnvelope Wrap(GameEvent gameEvent)
    {
        return new EventEnvelope { Sequence = gameEvent.Sequence, Type = EventTypes.NameOf(gameEvent), Payload = gameEvent };
    }

    private sealed class PositionConverter : JsonConverter<Position>
    {
        public override Position Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            JsonElement root = document.RootElement;
            return new Position(root.GetProperty("X").GetInt32(), root.GetProperty("Y").GetInt32());
        }

        public override void Write(Utf8JsonWriter writer, Position value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("X", value.X);
            writer.WriteNumber("Y", value.Y);
            writer.WriteEndObject();
        }
    }
}
