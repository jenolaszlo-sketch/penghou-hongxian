using FluentAssertions;
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;

namespace Penghou.Hongxian.Tests;

public sealed class SqliteSessionCatalogTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "hongxian-session-catalog-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ResourcePromotion_CommitsRevisionAndAuditReceiptAtomically()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new SqliteSessionCatalog(
            Path.Combine(rootPath, "promotion-catalog.db"), pooling: false);
        var session = await catalog.CreateAsync("repo", "resource", cancellationToken: ct);
        (await catalog.UpdateRevisionAsync(session.Id, null, "revision-1", ct))
            .Should().NotBeNull();
        var operation = new ExternalOperationReference("example-engine", Guid.CreateVersion7());
        await catalog.AttachExternalOperationAsync(session.Id, operation, ct);
        var promotedAt = DateTimeOffset.UtcNow;

        var promoted = await catalog.CommitRevisionPromotionAsync(
            session.Id,
            "revision-1",
            "revision-2",
            "mutation-7",
            operation,
            promotedAt,
            ct);

        promoted!.CurrentRevision.Should().Be("revision-2");
        var receipt = (await catalog.ListPendingAsync(cancellationToken: ct))
            .Single(item =>
                item.EventType == SessionEventTypes.RevisionPromoted);
        receipt.CorrelationId.Should().Be(operation.Id);
        receipt.CrossSystemRefs.Should().Contain(new Dictionary<string, string>
        {
            ["mutationId"] = "mutation-7",
            ["fromRevision"] = "revision-1",
            ["toRevision"] = "revision-2",
            ["auditSource"] = "transactional-revision-promotion"
        });

        (await catalog.CommitRevisionPromotionAsync(
            session.Id,
            "revision-1",
            "revision-3",
            "losing-mutation",
            operation,
            promotedAt,
            ct)).Should().BeNull();
        (await catalog.ListPendingAsync(cancellationToken: ct))
            .Should().NotContain(item =>
                item.CrossSystemRefs.GetValueOrDefault("mutationId") == "losing-mutation");
    }

    [Fact]
    public async Task Catalog_PersistsAndListsSessionsWithoutOpeningSessionStores()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(rootPath, "catalog.db");
        var first = new SqliteSessionCatalog(path, pooling: false);
        var olderId = SessionId.New();
        var newerId = SessionId.New();
        await first.CreateAsync("repo-a", $"resource:{olderId}", olderId, ct);
        await first.CreateAsync("repo-b", $"resource:{newerId}", newerId, ct);
        var operation = new ExternalOperationReference("example-engine", Guid.CreateVersion7());
        await first.AttachExternalOperationAsync(newerId, operation, ct);
        await first.UpdateRevisionAsync(newerId, null, "revision-1", ct);

        var reopened = new SqliteSessionCatalog(path, pooling: false);
        var sessions = await reopened.ListAsync(ct);
        var session = await reopened.FindByExternalOperationAsync(operation, ct);

        sessions.Should().HaveCount(2);
        session.Should().NotBeNull();
        session!.Id.Should().Be(newerId);
        session.ExternalOperations.Should().ContainSingle().Which.Should().Be(operation);
        session.CurrentRevision.Should().Be("revision-1");
        session.Version.Should().Be(2);
        (await reopened.ListPendingAsync(cancellationToken: ct))
            .Where(item => item.SessionId == newerId)
            .Select(item => item.EventType)
            .Should().Equal(
                SessionEventTypes.SessionCreated,
                SessionEventTypes.ExecutionAttached,
                SessionEventTypes.RevisionAccepted);
    }

    [Fact]
    public async Task AttachExternalOperation_RejectsASecondSessionOwnerAcrossCatalogInstances()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(rootPath, "catalog.db");
        var first = new SqliteSessionCatalog(path, pooling: false);
        var second = new SqliteSessionCatalog(path, pooling: false);
        var firstSession = await first.CreateAsync("repo", "resource:one", cancellationToken: ct);
        var secondSession = await first.CreateAsync("repo", "resource:two", cancellationToken: ct);
        var operation = new ExternalOperationReference("example-engine", Guid.CreateVersion7());
        await first.AttachExternalOperationAsync(firstSession.Id, operation, ct);

        var action = () => second.AttachExternalOperationAsync(secondSession.Id, operation, ct);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{firstSession.Id}*");
        (await second.FindByExternalOperationAsync(operation, ct))!.Id.Should().Be(firstSession.Id);
    }

    [Fact]
    public async Task ExternalOperationIdentity_IsScopedBySystem()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new SqliteSessionCatalog(Path.Combine(rootPath, "providers.db"), pooling: false);
        var firstSession = await catalog.CreateAsync("context", "resource:one", cancellationToken: ct);
        var secondSession = await catalog.CreateAsync("context", "resource:two", cancellationToken: ct);
        var sharedId = Guid.CreateVersion7();
        var first = new ExternalOperationReference("engine-a", sharedId);
        var second = new ExternalOperationReference("engine-b", sharedId);

        await catalog.AttachExternalOperationAsync(firstSession.Id, first, ct);
        await catalog.AttachExternalOperationAsync(secondSession.Id, second, ct);

        (await catalog.FindByExternalOperationAsync(first, ct))!.Id.Should().Be(firstSession.Id);
        (await catalog.FindByExternalOperationAsync(second, ct))!.Id.Should().Be(secondSession.Id);
    }

    [Fact]
    public async Task ResourceRevision_UsesCrossProcessCompareAndSwap()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(rootPath, "catalog.db");
        var first = new SqliteSessionCatalog(path, pooling: false);
        var second = new SqliteSessionCatalog(path, pooling: false);
        var session = await first.CreateAsync("repo", "resource", cancellationToken: ct);

        var results = await Task.WhenAll(
            first.UpdateRevisionAsync(session.Id, null, "revision-a", ct),
            second.UpdateRevisionAsync(session.Id, null, "revision-b", ct));

        results.Count(item => item is not null).Should().Be(1);
        var stored = await first.GetAsync(session.Id, ct);
        stored!.CurrentRevision.Should().BeOneOf("revision-a", "revision-b");
    }

    [Fact]
    public async Task DecisionLease_SerializesIndependentCatalogInstances()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(rootPath, "catalog.db");
        var first = new SqliteSessionCatalog(path, pooling: false);
        var second = new SqliteSessionCatalog(path, pooling: false);
        var session = await first.CreateAsync("repo", "resource", cancellationToken: ct);
        var held = await first.AcquireAsync(session.Id, Guid.CreateVersion7(), ct);

        var blocked = second.AcquireAsync(session.Id, Guid.CreateVersion7(), ct).AsTask();
        await Task.Delay(100, ct);
        blocked.IsCompleted.Should().BeFalse();

        await held.DisposeAsync();
        await using var acquired = await blocked.WaitAsync(TimeSpan.FromSeconds(2), ct);
        acquired.SessionId.Should().Be(session.Id);
    }

    [Fact]
    public async Task LifecycleReceipts_AreDurableAndMarkedDeliveredIdempotently()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new SqliteSessionCatalog(
            Path.Combine(rootPath, "catalog.db"),
            pooling: false);
        var session = await catalog.CreateAsync("repo", "resource", cancellationToken: ct);
        await using (var lease = await catalog.AcquireAsync(
            session.Id,
            Guid.CreateVersion7(),
            ct))
        {
        }
        var pending = await catalog.ListPendingAsync(cancellationToken: ct);
        pending.Select(item => item.EventType).Should().Equal(
            SessionEventTypes.SessionCreated,
            SessionEventTypes.DecisionLeaseAcquired,
            SessionEventTypes.DecisionLeaseReleased);

        await catalog.MarkDeliveredAsync(pending[0].ReceiptId, DateTimeOffset.UtcNow, ct);
        await catalog.MarkDeliveredAsync(pending[0].ReceiptId, DateTimeOffset.UtcNow.AddMinutes(1), ct);

        (await catalog.ListPendingAsync(cancellationToken: ct))
            .Should().HaveCount(2);
    }

    public void Dispose()
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }
}
