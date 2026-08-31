using System.Text.Json;
using FluentAssertions;
using Penghou.Hongxian.Sqlite;

namespace Penghou.Hongxian.Tests;

public sealed class SessionEventPayloadApiTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "hongxian-payload-api-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TypedAndJsonElementAppends_ShareCanonicalIdentityAndTypedReads()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var request = new SessionEventRequest(
            SessionId.New(),
            Participant("user"),
            SessionEventTypes.UserMessage,
            DateTimeOffset.UtcNow,
            IdempotencyKey: "message-1",
            PayloadSchema: new SessionPayloadSchema("chat.message", 1));

        var typed = await store.AppendAsync(
            request,
            new MessagePayload("hello", 2),
            cancellationToken: ct);
        using var json = JsonDocument.Parse("{ \"count\": 2.00, \"text\": \"hello\" }");
        var replay = await store.AppendAsync(request, json.RootElement, ct);

        replay.EventId.Should().Be(typed.EventId);
        replay.Sequence.Should().Be(typed.Sequence);
        replay.Hash.Should().Be(typed.Hash);
        replay.ReadPayload().GetRawText().Should().Be(typed.ReadPayload().GetRawText());
        typed.PayloadJson.Should().BeNull();
        typed.ReadPayload<MessagePayload>().Should().Be(new MessagePayload("hello", 2));
    }

    [Fact]
    public async Task DigestOnlyJsonPayload_UsesCanonicalJsonDigestAndCannotBeRead()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var request = new SessionEventRequest(
            SessionId.New(),
            Participant("user"),
            SessionEventTypes.UserMessage,
            DateTimeOffset.UtcNow,
            PayloadRetention: SessionPayloadRetention.DigestOnly);

        var committed = await store.AppendAsync(
            request,
            new MessagePayload("secret", 1),
            cancellationToken: ct);

        committed.Payload.Should().BeNull();
        committed.PayloadDigest.Should().StartWith("sha256:penghou-canonical-json:v1:");
        var read = () => committed.ReadPayload();
        read.Should().Throw<SessionPayloadUnavailableException>()
            .Which.Retention.Should().Be(SessionPayloadRetention.DigestOnly);
    }

    [Fact]
    public async Task TypedAppendWithDelivery_ReportsWhenProjectionIsNotConfigured()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var concreteStore = new SimingSessionEventStore(rootPath);
        ISessionEventDeliveryStore store = concreteStore;
        var result = await store.AppendWithDeliveryAsync(
            new SessionEventRequest(
                SessionId.New(),
                Participant("agent"),
                SessionEventTypes.AssistantMessage,
                DateTimeOffset.UtcNow),
            new MessagePayload("done", 1),
            cancellationToken: ct);

        result.ProjectionDelivery.Outcome.Should().Be(
            SessionProjectionDeliveryOutcome.NotConfigured);
        result.ProjectionDelivery.DeliveryStatusRecorded.Should().BeFalse();
        result.Event.ReadPayload<MessagePayload>().Should().Be(
            new MessagePayload("done", 1));
    }

    private sealed record MessagePayload(string Text, decimal Count);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }
}
