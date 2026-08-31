using System.Text.Json;
using System.Text.Json.Serialization;

namespace Penghou.Hongxian;

internal sealed class SessionIdJsonConverter : JsonConverter<SessionId>
{
    public override SessionId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String &&
        SessionId.TryParse(reader.GetString(), out var value)
            ? value
            : throw new JsonException("Session ID must be a non-empty UUID string.");

    public override void Write(
        Utf8JsonWriter writer,
        SessionId value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

internal sealed class CrossStoreOperationIdJsonConverter : JsonConverter<CrossStoreOperationId>
{
    public override CrossStoreOperationId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String &&
        CrossStoreOperationId.TryParse(reader.GetString(), out var value)
            ? value
            : throw new JsonException(
                "Cross-store operation ID must be a non-empty UUID string.");

    public override void Write(
        Utf8JsonWriter writer,
        CrossStoreOperationId value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

internal sealed class ExternalOperationReferenceJsonConverter :
    JsonConverter<ExternalOperationReference>
{
    public override ExternalOperationReference Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String &&
        ExternalOperationReference.TryParse(reader.GetString(), out var value)
            ? value
            : throw new JsonException(
                "External operation reference must use the bounded 'system:id' string format.");

    public override void Write(
        Utf8JsonWriter writer,
        ExternalOperationReference value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
