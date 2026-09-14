using System.Text.Json;
using System.Text.Json.Serialization;

namespace Penghou.Hongxian;

internal sealed class ExperienceEntityIdJsonConverter : JsonConverter<ExperienceEntityId>
{
    public override ExperienceEntityId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && ExperienceEntityId.TryParse(reader.GetString(), out var value)
            ? value
            : throw new JsonException("Experience entity ID must be a non-empty UUID string.");

    public override void Write(Utf8JsonWriter writer, ExperienceEntityId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

internal sealed class ExperienceRelationIdJsonConverter : JsonConverter<ExperienceRelationId>
{
    public override ExperienceRelationId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && ExperienceRelationId.TryParse(reader.GetString(), out var value)
            ? value
            : throw new JsonException("Experience relation ID must be a non-empty UUID string.");

    public override void Write(Utf8JsonWriter writer, ExperienceRelationId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

internal sealed class ExperienceEvidenceReferenceIdJsonConverter : JsonConverter<ExperienceEvidenceReferenceId>
{
    public override ExperienceEvidenceReferenceId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && ExperienceEvidenceReferenceId.TryParse(reader.GetString(), out var value)
            ? value
            : throw new JsonException("Experience evidence reference ID must be a non-empty UUID string.");

    public override void Write(Utf8JsonWriter writer, ExperienceEvidenceReferenceId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

internal sealed class ExperienceDerivationIdJsonConverter : JsonConverter<ExperienceDerivationId>
{
    public override ExperienceDerivationId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && ExperienceDerivationId.TryParse(reader.GetString(), out var value)
            ? value
            : throw new JsonException("Experience derivation ID must be a non-empty UUID string.");

    public override void Write(Utf8JsonWriter writer, ExperienceDerivationId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
