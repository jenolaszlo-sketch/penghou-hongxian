using System.Text.Json;
using FluentAssertions;

namespace Penghou.Hongxian.Tests;

public sealed class SessionPayloadUpcasterTests
{
    [Fact]
    public void Upcast_AppliesACompleteOrderedChainWithoutChangingTheSource()
    {
        using var document = JsonDocument.Parse("{\"message\":\"hello\"}");
        var source = document.RootElement.Clone();
        var registry = new SessionPayloadUpcasterRegistry([
            new DelegateUpcaster("chat.message", 1, payload => JsonSerializer.SerializeToElement(new
            {
                text = payload.GetProperty("message").GetString()
            })),
            new DelegateUpcaster("chat.message", 2, payload => JsonSerializer.SerializeToElement(new
            {
                text = payload.GetProperty("text").GetString(),
                format = "plain"
            }))
        ]);

        var result = registry.Upcast(new SessionPayloadSchema("chat.message", 1), source, 3);

        result.Schema.Should().Be(new SessionPayloadSchema("chat.message", 3));
        result.Payload.GetProperty("text").GetString().Should().Be("hello");
        result.Payload.GetProperty("format").GetString().Should().Be("plain");
        source.GetProperty("message").GetString().Should().Be("hello");
    }

    [Fact]
    public void Upcast_ReportsTheMissingStepAsTypedData()
    {
        using var document = JsonDocument.Parse("{}");
        var registry = new SessionPayloadUpcasterRegistry();

        var action = () => registry.Upcast(
            new SessionPayloadSchema("chat.message", 1),
            document.RootElement,
            2);

        var failure = action.Should().Throw<UnsupportedSessionPayloadSchemaException>().Which;
        failure.SourceSchema.Should().Be(new SessionPayloadSchema("chat.message", 1));
        failure.RequestedVersion.Should().Be(2);
        failure.MissingSourceVersion.Should().Be(1);
    }

    [Fact]
    public void Constructor_RejectsDuplicateAndSkippingUpcasters()
    {
        var duplicate = () => new SessionPayloadUpcasterRegistry([
            new DelegateUpcaster("chat.message", 1, payload => payload),
            new DelegateUpcaster("chat.message", 1, payload => payload)
        ]);
        duplicate.Should().Throw<ArgumentException>().WithMessage("*already registered*");

        var skipping = () => new SessionPayloadUpcasterRegistry([
            new DelegateUpcaster("chat.message", 1, payload => payload, targetVersion: 3)
        ]);
        skipping.Should().Throw<ArgumentException>().WithMessage("*exactly one*");
    }

    private sealed class DelegateUpcaster(
        string schemaName,
        int sourceVersion,
        Func<JsonElement, JsonElement> upcast,
        int? targetVersion = null) : ISessionPayloadUpcaster
    {
        public string SchemaName { get; } = schemaName;

        public int SourceVersion { get; } = sourceVersion;

        public int TargetVersion { get; } = targetVersion ?? sourceVersion + 1;

        public JsonElement Upcast(JsonElement payload) => upcast(payload);
    }
}
