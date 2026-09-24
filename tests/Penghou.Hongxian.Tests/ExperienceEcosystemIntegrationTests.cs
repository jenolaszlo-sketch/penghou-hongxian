using System.Text.Json;
using FluentAssertions;
using Penghou.Hongxian;
using Penghou.Hongxian.LatticeDb;
using Penghou.Hongxian.Sqlite;

namespace Penghou.Hongxian.Tests;

/// <summary>
/// Executable proof for the ecosystem integration guide: Fuwen-style
/// planning evidence with conditional activation, and Baize-style bounded
/// invocation outcomes with reproducible advisory reads. The flows use only
/// portable Hongxian contracts plus the standard SQLite/LatticeDB providers;
/// no sibling package or provider-specific type crosses the flow.
/// </summary>
public sealed class ExperienceEcosystemIntegrationTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(), "hongxian-ecosystem-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FuwenStyle_PlanEvidenceRecallAndActivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var hongxian = new HongxianSqliteStoreSet(new HongxianSqliteOptions
        {
            RootPath = Path.Combine(rootPath, "hongxian"),
            Pooling = false
        });
        var session = await hongxian.SessionStore.CreateAsync(
            "fuwen-project", "resource/plan-1", cancellationToken: ct);
        var plan = new ExternalOperationReference("fuwen", "plan/7");
        await hongxian.SessionStore.AttachExternalOperationAsync(session.Id, plan, ct);

        var proposed = await hongxian.EventStore.AppendAsync(
            new SessionEventRequest(
                session.Id,
                SessionParticipantAttribution.Agent("planner", "fuwen-host"),
                "plan-proposed",
                DateTimeOffset.UtcNow,
                CrossSystemRefs: new Dictionary<string, string>
                {
                    ["fuwen-plan"] = "plan/7",
                    ["repo"] = "repo@abc123",
                    ["model"] = "model/x",
                    ["policy"] = "policy/1"
                },
                IdempotencyKey: $"session:{session.Id}:plan-proposed"),
            new { text = "durable plan revision evidence" },
            cancellationToken: ct);

        // Project the verified history through a host-owned mapping and recall it.
        var history = await hongxian.EventStore.ReadVerifiedHistoryAsync(session.Id, ct);
        using var provider = new LatticeDbExperienceProvider(
            new LatticeDbExperienceProviderOptions
            {
                Location = LatticeDbLocationKind.Memory,
                Lock = false
            });
        var projection = new ExperienceProjectionDescriptor(
            ExperienceProjectionId.New(), "latticedb", "ecosystem-host", 1, "policy-1");
        await ProjectMessagesAsync(provider, projection, history, ct);

        var request = new ExperienceBoundedRecallRequest(
            projection.ProjectionId,
            new ExperienceRetrievalPolicy("planning-recall", "policy-1"),
            "durable");
        var result = await ExperienceBoundedRecall.ExecuteAsync(provider, request, ct);
        result.Completion.Should().Be(ExperienceBoundedRecallCompletion.Completed);
        result.Items.Should().ContainSingle();
        result.Items[0].Record.Evidence.Should().ContainSingle()
            .Which.EventId.Should().Be(proposed.EventId);

        var receipt = ExperienceRecallReceipt.Create(
            result, ExperienceRecallReceipts.HashSuppliedContext("plan context"));
        var receiptEvent = await hongxian.EventStore.AppendRecallReceiptAsync(
            session.Id,
            SessionParticipantAttribution.Agent("planner", "fuwen-host"),
            receipt,
            DateTimeOffset.UtcNow,
            causationId: proposed.EventId,
            cancellationToken: ct);
        receiptEvent.ReadRecallReceipt().QueryFingerprint.Should().Be(result.QueryFingerprint);

        // Conditional activation: fresh head commits, stale head conflicts.
        var fresh = (await hongxian.EventStore.ReadVerifiedHistoryAsync(session.Id, ct)).VerifiedHead;
        var activated = await hongxian.EventStore.AppendAsync(
            new SessionEventRequest(
                session.Id,
                SessionParticipantAttribution.Agent("planner", "fuwen-host"),
                "plan-activated",
                DateTimeOffset.UtcNow,
                CausationId: receiptEvent.EventId,
                ExpectedHead: fresh),
            new { text = "durable generation activated" },
            cancellationToken: ct);
        activated.CausationId.Should().Be(receiptEvent.EventId);
        var staleActivation = () => hongxian.EventStore.AppendAsync(
            new SessionEventRequest(
                session.Id,
                SessionParticipantAttribution.Agent("planner", "fuwen-host"),
                "plan-activated",
                DateTimeOffset.UtcNow,
                ExpectedHead: fresh),
            new { text = "stale generation activation" },
            cancellationToken: ct);
        await staleActivation.Should().ThrowAsync<SessionLedgerHeadConflictException>();

        await hongxian.CatalogEvidence.DispatchPendingAsync(cancellationToken: ct);
        var audit = await hongxian.ConsistencyAudit.InspectAsync(session.Id, ct);
        audit.Health.Should().Be(SessionConsistencyHealth.Healthy);
    }

    [Fact]
    public async Task BaizeStyle_InvocationOutcomesStayBoundedAndReproducible()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var sessions = new SimingSessionEventStore(Path.Combine(rootPath, "siming"));
        var sessionId = SessionId.New();
        await sessions.AppendAsync(new SessionEventRequest(
            sessionId,
            SessionParticipantAttribution.System("baize-host", "tests"),
            SessionEventTypes.SessionCreated,
            DateTimeOffset.UtcNow,
            IdempotencyKey: $"session:{sessionId}:created"), ct);

        // Bounded outcome: normalized usage and classification, never prompts.
        var invoked = await sessions.AppendAsync(
            new SessionEventRequest(
                sessionId,
                SessionParticipantAttribution.System("baize-host", "tests"),
                "baize.invocation-completed",
                DateTimeOffset.UtcNow,
                CrossSystemRefs: new Dictionary<string, string>
                {
                    ["baize-invocation"] = "inv/9",
                    ["model"] = "model/x"
                },
                IdempotencyKey: $"session:{sessionId}:inv-9"),
            new
            {
                text = "durable invocation outcome",
                provider = "example",
                model = "model/x",
                outcome = "completed",
                usageTokens = 128,
                latencyMs = 42
            },
            cancellationToken: ct);
        invoked.ReadPayload<JsonElement>().TryGetProperty("prompt", out _).Should().BeFalse();

        var history = await sessions.ReadVerifiedHistoryAsync(sessionId, ct);
        using var provider = new LatticeDbExperienceProvider(
            new LatticeDbExperienceProviderOptions
            {
                Location = LatticeDbLocationKind.Memory,
                Lock = false
            });
        var projection = new ExperienceProjectionDescriptor(
            ExperienceProjectionId.New(), "latticedb", "ecosystem-host", 1, "policy-1");
        await ProjectMessagesAsync(provider, projection, history, ct);

        var request = new ExperienceBoundedRecallRequest(
            projection.ProjectionId,
            new ExperienceRetrievalPolicy("routing-advisory", "policy-1"),
            "durable");
        var first = await ExperienceBoundedRecall.ExecuteAsync(provider, request, ct);
        var replay = await ExperienceBoundedRecall.ExecuteAsync(provider, request, ct);

        // Advisory reads reproduce their inputs instead of shifting with time.
        replay.QueryFingerprint.Should().Be(first.QueryFingerprint);
        replay.Items.Select(item => item.Record.Id)
            .Should().Equal(first.Items.Select(item => item.Record.Id));
        first.Items.Should().ContainSingle().Which.Record.Evidence.Should().ContainSingle()
            .Which.EventId.Should().Be(invoked.EventId);

        var receipt = ExperienceRecallReceipt.Create(
            first, ExperienceRecallReceipts.HashSuppliedContext("routing context"));
        var receiptEvent = await sessions.AppendRecallReceiptAsync(
            sessionId,
            SessionParticipantAttribution.System("baize-host", "tests"),
            receipt,
            DateTimeOffset.UtcNow,
            cancellationToken: ct);
        receiptEvent.ReadRecallReceipt().SuppliedContextDigest
            .Should().Be(receipt.SuppliedContextDigest);
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }

    private static async Task ProjectMessagesAsync(
        LatticeDbExperienceProvider provider,
        ExperienceProjectionDescriptor projection,
        VerifiedSessionHistory history,
        CancellationToken cancellationToken)
    {
        foreach (var sessionEvent in history.Events)
        {
            if (sessionEvent.Sequence <= 0)
                continue;
            string? text;
            try
            {
                text = sessionEvent.ReadPayload<JsonElement>().GetProperty("text").GetString();
            }
            catch (Exception exception) when (
                exception is KeyNotFoundException or
                    InvalidOperationException or
                    JsonException or
                    SessionPayloadUnavailableException or
                    SessionPayloadFormatException)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
                continue;
            var entity = new ExperienceEntity(
                ExperienceEntityId.CreateDeterministic(
                    projection.ProjectionId, "note", $"event:{sessionEvent.Sequence}"),
                "note",
                new ExperienceProvenance(
                    projection,
                    ExperienceDerivationId.CreateDeterministic(
                        projection.ProjectionId, "ecosystem-host", $"event:{sessionEvent.Sequence}")),
                new Dictionary<string, JsonElement>
                {
                    ["text"] = JsonSerializer.SerializeToElement(text)
                },
                evidence:
                [
                    new ExperienceEvidenceReference(
                        history.SessionId,
                        history.VerifiedHead.LedgerIdentity,
                        sessionEvent.Sequence,
                        sessionEvent.EventId,
                        sessionEvent.Hash)
                ]);
            (await provider.UpsertEntityAsync(entity, cancellationToken)).Outcome
                .Should().BeOneOf(
                    ExperienceModelWriteOutcome.Applied,
                    ExperienceModelWriteOutcome.AlreadyPresent);
        }
    }
}
