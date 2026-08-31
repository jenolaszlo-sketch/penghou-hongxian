using FluentAssertions;
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;
using Microsoft.Data.Sqlite;
using Penghou.Siming;
using Penghou.Siming.Sqlite;
using System.Text.Json;

namespace Penghou.Hongxian.Tests;

public sealed class SimingSessionEventStoreTests : IDisposable
{
    private readonly string rootPath = Path.Combine(Path.GetTempPath(), "hongxian-siming-session-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Append_ReopenAndVerify_PreservesDomainEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionId = SessionId.New();
        var correlationId = Guid.CreateVersion7();
        SessionEvent first;
        await using (var writer = new SimingSessionEventStore(rootPath))
        {
            first = await writer.AppendAsync(new SessionEventRequest(
                sessionId, Participant("user"), SessionEventTypes.UserMessage, DateTimeOffset.UtcNow,
                CorrelationId: correlationId, PayloadJson: "{\"prompt\":\"hello\"}",
                PayloadSchema: new SessionPayloadSchema("guyabano.user-message", 1)), ct);
        }

        await using var reader = new SimingSessionEventStore(rootPath);
        var events = await reader.ReadAsync(sessionId, cancellationToken: ct);
        events.Should().ContainSingle().Which.Should().BeEquivalentTo(first);
        first.SchemaVersion.Should().Be(SessionEventEnvelopeSchema.CurrentVersion);
        first.PayloadSchema.Should().Be(new SessionPayloadSchema("guyabano.user-message", 1));
        first.CommittedAt.Should().NotBe(default);
        File.Exists(reader.GetLedgerPath(sessionId)).Should().BeTrue();
        (await reader.VerifyChainAsync(sessionId, ct)).Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task Read_Preview1Envelope_UpgradesActorToLegacyAttributionWithoutRewriting()
    {
        var ct = TestContext.Current.CancellationToken;
        var sessionId = SessionId.New();
        var ledgerPath = Path.Combine(rootPath, sessionId.ToString(), "session.db");
        Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
        await using (var legacyLedger = new SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
            new SimingSqliteOptions { DatabasePath = ledgerPath, Pooling = false },
            new CanonicalJsonPayloadSerializer()))
        {
            await legacyLedger.AppendAsync(new LedgerAppendRequest<Preview1SessionEventPayload>(
                sessionId.ToString(),
                SessionEventTypes.UserMessage,
                new Preview1SessionEventPayload(
                    1,
                    Guid.CreateVersion7(),
                    "legacy-user",
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    "legacy:user-message:1",
                    null,
                    "hello",
                    SessionPayloadSensitivity.Internal,
                    SessionPayloadRetention.Retain,
                    null),
                "legacy:user-message:1"), ct);
        }

        await using var store = new SimingSessionEventStore(rootPath);
        var restored = (await store.ReadAsync(sessionId, cancellationToken: ct)).Single();

        restored.SchemaVersion.Should().Be(1);
        restored.Participant.Should().Be(new SessionParticipantAttribution(
            SessionParticipantKinds.Legacy,
            "hongxian-preview-1",
            "legacy-user"));

        var replay = await store.AppendAsync(new SessionEventRequest(
            sessionId,
            SessionParticipantAttribution.Human("legacy-user", "new-host"),
            SessionEventTypes.UserMessage,
            DateTimeOffset.UtcNow,
            PayloadJson: "hello",
            IdempotencyKey: "legacy:user-message:1"), ct);
        replay.EventId.Should().Be(restored.EventId);
        (await store.ReadAsync(sessionId, cancellationToken: ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task Append_UsesOneIndependentContiguousChainPerSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var firstSession = SessionId.New();
        var secondSession = SessionId.New();
        var first = await store.AppendAsync(new SessionEventRequest(
            firstSession, Participant("user"), SessionEventTypes.UserMessage, DateTimeOffset.UtcNow), ct);
        var second = await store.AppendAsync(new SessionEventRequest(
            secondSession, Participant("hongxian"), SessionEventTypes.ExecutionStarted, DateTimeOffset.UtcNow), ct);

        first.Sequence.Should().Be(1);
        second.Sequence.Should().Be(1);
        first.PreviousHash.Should().BeNull();
        second.PreviousHash.Should().BeNull();
        store.GetLedgerPath(firstSession).Should().NotBe(store.GetLedgerPath(secondSession));
    }

    [Fact]
    public async Task Append_IdempotencyIsScopedPerSessionAndRetrySafe()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var firstSession = SessionId.New();
        var secondSession = SessionId.New();
        var request = new SessionEventRequest(firstSession, Participant("hongxian"), SessionEventTypes.OperationPrepared,
            DateTimeOffset.UtcNow, IdempotencyKey: "operation:prepared");

        var first = await store.AppendAsync(request, ct);
        var replay = await store.AppendAsync(request with { OccurredAt = request.OccurredAt.AddMinutes(1) }, ct);
        var otherSession = await store.AppendAsync(request with { SessionId = secondSession }, ct);

        replay.Should().BeEquivalentTo(first);
        otherSession.EventId.Should().NotBe(first.EventId);
        otherSession.Sequence.Should().Be(1);
        (await store.ReadAsync(firstSession, cancellationToken: ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task ConditionalAppend_RejectsStaleHeadButAllowsIdempotentRetry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var sessionId = SessionId.New();
        var emptyHead = (await store.ReadVerifiedHistoryAsync(sessionId, ct)).VerifiedHead;
        var request = new SessionEventRequest(
            sessionId,
            Participant("user"),
            SessionEventTypes.UserMessage,
            DateTimeOffset.UtcNow,
            PayloadJson: "{\"message\":\"first\"}",
            IdempotencyKey: "message:first",
            ExpectedHead: emptyHead);

        var first = await store.AppendAsync(request, ct);
        var second = await store.AppendAsync(new SessionEventRequest(
            sessionId,
            Participant("assistant"),
            SessionEventTypes.AssistantMessage,
            DateTimeOffset.UtcNow), ct);

        var stale = () => store.AppendAsync(new SessionEventRequest(
            sessionId,
            Participant("user"),
            SessionEventTypes.UserMessage,
            DateTimeOffset.UtcNow,
            ExpectedHead: emptyHead), ct);
        var conflict = await stale.Should().ThrowAsync<SessionLedgerHeadConflictException>();
        conflict.Which.ExpectedHead.Should().Be(emptyHead);
        conflict.Which.ActualHead.Sequence.Should().Be(second.Sequence);
        conflict.Which.ActualHead.Hash.Should().Be(second.Hash);

        var replay = await store.AppendAsync(
            request with { OccurredAt = request.OccurredAt.AddMinutes(1) }, ct);
        replay.Should().BeEquivalentTo(first);
        (await store.ReadAsync(sessionId, cancellationToken: ct)).Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadPage_ReturnsBoundedStableCursor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var sessionId = SessionId.New();
        for (var index = 0; index < 5; index++)
            await store.AppendAsync(new SessionEventRequest(
                sessionId, Participant("hongxian"), $"event-{index}", DateTimeOffset.UtcNow), ct);

        var first = await store.ReadPageAsync(new SessionEventPageRequest(sessionId, Limit: 2), ct);
        var second = await store.ReadPageAsync(new SessionEventPageRequest(sessionId, first.NextSequence!.Value, 2), ct);
        var third = await store.ReadPageAsync(new SessionEventPageRequest(sessionId, second.NextSequence!.Value, 2), ct);

        first.Events.Select(item => item.Sequence).Should().Equal(1, 2);
        second.Events.Select(item => item.Sequence).Should().Equal(3, 4);
        third.Events.Select(item => item.Sequence).Should().Equal(5);
        first.HasMore.Should().BeTrue();
        second.HasMore.Should().BeTrue();
        third.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Append_WhenProjectionFails_RetryReplaysCommittedEventAndHealsProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        var projection = new FailOnceProjectionStore();
        await using var store = new SimingSessionEventStore(rootPath, projectionStore: projection);
        var sessionId = SessionId.New();
        var request = new SessionEventRequest(
            sessionId,
            Participant("hongxian"),
            SessionEventTypes.ExecutionStarted,
            DateTimeOffset.UtcNow,
            IdempotencyKey: "workflow:start");

        var committed = await store.AppendAsync(request, ct);

        committed.Sequence.Should().Be(1);
        projection.Applied.Should().BeEmpty();
        projection.Attempts.Should().Be(1);

        var replay = await store.AppendAsync(request, ct);

        replay.Sequence.Should().Be(1);
        (await store.ReadAsync(sessionId, cancellationToken: ct)).Should().ContainSingle();
        projection.Applied.Should().ContainSingle().Which.Should().BeEquivalentTo(replay);
        projection.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task Append_WhenTrackedProjectionFails_ReturnsCommitAndExposesRepairableLag()
    {
        var ct = TestContext.Current.CancellationToken;
        var durable = new SqliteSessionProjectionStore(
            Path.Combine(rootPath, "projection.db"), pooling: false);
        var projection = new FailOnceDeliveryProjectionStore(durable);
        await using var store = new SimingSessionEventStore(
            Path.Combine(rootPath, "sessions"), projectionStore: projection);
        var sessionId = SessionId.New();
        var request = new SessionEventRequest(
            sessionId,
            Participant("hongxian"),
            SessionEventTypes.ExecutionStarted,
            DateTimeOffset.UtcNow,
            IdempotencyKey: "workflow:tracked-start");

        var committed = await store.AppendAsync(request, ct);
        var lagging = await durable.GetDeliveryStatusAsync(sessionId, ct);

        committed.Sequence.Should().Be(1);
        lagging!.IsLagging.Should().BeTrue();
        lagging.LastFailureType.Should().Be(typeof(InvalidOperationException).FullName);

        var replay = await store.AppendAsync(request, ct);
        var healed = await durable.GetDeliveryStatusAsync(sessionId, ct);
        replay.Should().BeEquivalentTo(committed);
        healed!.IsLagging.Should().BeFalse();
        healed.LastFailureType.Should().BeNull();
    }

    [Fact]
    public async Task Append_DigestOnlyPayload_RedactsContentAndKeepsRetryIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var sessionId = SessionId.New();
        var request = new SessionEventRequest(
            sessionId,
            Participant("user"),
            SessionEventTypes.UserMessage,
            DateTimeOffset.UtcNow,
            PayloadJson: "a sensitive clarification",
            IdempotencyKey: "message:1",
            PayloadSensitivity: SessionPayloadSensitivity.Confidential,
            PayloadRetention: SessionPayloadRetention.DigestOnly);

        var committed = await store.AppendAsync(request, ct);
        var replay = await store.AppendAsync(request with { OccurredAt = request.OccurredAt.AddMinutes(1) }, ct);

        committed.PayloadJson.Should().BeNull();
        committed.PayloadDigest.Should().StartWith("sha256:utf8:v1:");
        committed.PayloadSensitivity.Should().Be(SessionPayloadSensitivity.Confidential);
        committed.PayloadRetention.Should().Be(SessionPayloadRetention.DigestOnly);
        replay.Should().BeEquivalentTo(committed);

        var conflict = () => store.AppendAsync(request with { PayloadJson = "different" }, ct);
        var failure = (await conflict.Should()
            .ThrowAsync<SessionEventIdempotencyConflictException>()).Which;
        failure.IdempotencyKey.Should().Be(request.IdempotencyKey);
        failure.ExistingEventId.Should().Be(committed.EventId);
    }

    [Fact]
    public async Task Append_OmittedPayload_PersistsNeitherContentNorDigest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var committed = await store.AppendAsync(new SessionEventRequest(
            SessionId.New(),
            Participant("user"),
            SessionEventTypes.UserMessage,
            DateTimeOffset.UtcNow,
            PayloadJson: "do not persist",
            PayloadSensitivity: SessionPayloadSensitivity.Restricted,
            PayloadRetention: SessionPayloadRetention.Omit), ct);

        committed.PayloadJson.Should().BeNull();
        committed.PayloadDigest.Should().BeNull();
        committed.PayloadRetention.Should().Be(SessionPayloadRetention.Omit);
    }

    [Fact]
    public async Task BoundedLedgerCache_EvictsIdleHandlesAndReopensTheChain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(
            rootPath,
            maximumCachedLedgers: 1);
        var firstSession = SessionId.New();
        var secondSession = SessionId.New();
        await store.AppendAsync(new SessionEventRequest(
            firstSession,
            Participant("user"),
            SessionEventTypes.UserMessage,
            DateTimeOffset.UtcNow), ct);
        await store.AppendAsync(new SessionEventRequest(
            secondSession,
            Participant("user"),
            SessionEventTypes.UserMessage,
            DateTimeOffset.UtcNow), ct);

        var reopened = await store.AppendAsync(new SessionEventRequest(
            firstSession,
            Participant("hongxian"),
            SessionEventTypes.AssistantMessage,
            DateTimeOffset.UtcNow), ct);

        reopened.Sequence.Should().Be(2);
        reopened.PreviousHash.Should().NotBeNull();
        (await store.VerifyChainAsync(firstSession, ct))!.Sequence.Should().Be(2);
    }

    [Fact]
    public async Task Append_RejectsDefaultSessionAndCorrelationIds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);

        var defaultSession = () => store.AppendAsync(new SessionEventRequest(
            default,
            Participant("user"),
            SessionEventTypes.UserMessage,
            DateTimeOffset.UtcNow), ct);
        await defaultSession.Should().ThrowAsync<ArgumentException>();

        var defaultCorrelation = () => store.AppendAsync(new SessionEventRequest(
            SessionId.New(),
            Participant("user"),
            SessionEventTypes.UserMessage,
            DateTimeOffset.UtcNow,
            CorrelationId: Guid.Empty), ct);
        await defaultCorrelation.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Append_RejectsUnboundedOrIncompleteAttributionAndReferences()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var template = new SessionEventRequest(
            SessionId.New(),
            Participant("user"),
            SessionEventTypes.UserMessage,
            DateTimeOffset.UtcNow);

        var missingSubject = () => store.AppendAsync(template with
        {
            Participant = SessionParticipantAttribution.Human(" ")
        }, ct);
        var longProvider = () => store.AppendAsync(template with
        {
            Participant = SessionParticipantAttribution.Human(
                "user",
                new string('p', SessionContractLimits.ParticipantProviderCharacters + 1))
        }, ct);
        var tooManyReferences = () => store.AppendAsync(template with
        {
            CrossSystemRefs = Enumerable.Range(
                    0,
                    SessionContractLimits.CrossSystemReferenceCount + 1)
                .ToDictionary(index => $"ref-{index}", index => index.ToString())
        }, ct);

        await missingSubject.Should().ThrowAsync<ArgumentException>();
        await longProvider.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await tooManyReferences.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Append_IdempotencyIncludesCompleteParticipantAttribution()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var store = new SimingSessionEventStore(rootPath);
        var request = new SessionEventRequest(
            SessionId.New(),
            SessionParticipantAttribution.Agent("planner", "baize", "Planner"),
            SessionEventTypes.AssistantMessage,
            DateTimeOffset.UtcNow,
            IdempotencyKey: "message:planner:1");

        await store.AppendAsync(request, ct);
        var conflict = () => store.AppendAsync(request with
        {
            Participant = request.Participant with { Provider = "another-provider" }
        }, ct);

        await conflict.Should().ThrowAsync<SessionEventIdempotencyConflictException>();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
    }

    private sealed class FailOnceProjectionStore : ISessionProjectionStore
    {
        public int Attempts { get; private set; }

        public List<SessionEvent> Applied { get; } = [];

        public Task ApplyAsync(SessionEvent sessionEvent, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts == 1) throw new InvalidOperationException("projection unavailable");
            Applied.Add(sessionEvent);
            return Task.CompletedTask;
        }

        public Task<SessionProjectionSnapshot?> GetAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionProjectionSnapshot?>(null);

        public Task<IReadOnlyList<SessionProjectionSnapshot>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionProjectionSnapshot>>([]);

        public Task<SessionProjectionSnapshot?> RebuildAsync(
            VerifiedSessionHistory history,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SessionProjectionSnapshot?>(null);
    }

    private sealed record Preview1SessionEventPayload(
        int SchemaVersion,
        Guid EventId,
        string Actor,
        DateTimeOffset OccurredAt,
        Guid? CausationId,
        Guid? CorrelationId,
        string? IdempotencyKey,
        IReadOnlyDictionary<string, string>? CrossSystemRefs,
        string? PayloadJson,
        SessionPayloadSensitivity PayloadSensitivity,
        SessionPayloadRetention PayloadRetention,
        string? PayloadDigest,
        SessionPayloadSchema? PayloadSchema = null,
        JsonElement? Payload = null);

    private sealed class FailOnceDeliveryProjectionStore(
        SqliteSessionProjectionStore inner) :
        ISessionProjectionStore,
        ISessionProjectionDeliveryStore
    {
        private int attempts;

        public Task ApplyAsync(
            SessionEvent sessionEvent,
            CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new InvalidOperationException("projection unavailable"))
                : inner.ApplyAsync(sessionEvent, cancellationToken);

        public Task<SessionProjectionSnapshot?> GetAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default) =>
            inner.GetAsync(sessionId, cancellationToken);

        public Task<IReadOnlyList<SessionProjectionSnapshot>> ListAsync(
            CancellationToken cancellationToken = default) =>
            inner.ListAsync(cancellationToken);

        public Task<SessionProjectionSnapshot?> RebuildAsync(
            VerifiedSessionHistory history,
            CancellationToken cancellationToken = default) =>
            inner.RebuildAsync(history, cancellationToken);

        public Task RecordCommittedAsync(
            SessionEvent sessionEvent,
            CancellationToken cancellationToken = default) =>
            inner.RecordCommittedAsync(sessionEvent, cancellationToken);

        public Task RecordFailureAsync(
            SessionEvent sessionEvent,
            Exception exception,
            CancellationToken cancellationToken = default) =>
            inner.RecordFailureAsync(sessionEvent, exception, cancellationToken);

        public Task<SessionProjectionDeliveryStatus?> GetDeliveryStatusAsync(
            SessionId sessionId,
            CancellationToken cancellationToken = default) =>
            inner.GetDeliveryStatusAsync(sessionId, cancellationToken);

        public Task<IReadOnlyList<SessionProjectionDeliveryStatus>> ListLaggingAsync(
            int maximumCount = 100,
            CancellationToken cancellationToken = default) =>
            inner.ListLaggingAsync(maximumCount, cancellationToken);
    }
}
