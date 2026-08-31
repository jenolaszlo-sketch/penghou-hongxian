using FluentAssertions;
using Microsoft.Data.Sqlite;
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
    public async Task ResourceVersionCompareAndSwap_CommitsAuditReceiptAtomically()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = new SqliteSessionCatalog(
            Path.Combine(rootPath, "version-catalog.db"), pooling: false);
        var session = await catalog.CreateAsync("repo", "resource", cancellationToken: ct);
        var accepted = await catalog.UpdateRevisionAsync(
            session.Id, null, "resource-version-1", ct);

        accepted!.CurrentRevision.Should().Be("resource-version-1");
        var receipt = (await catalog.ListPendingAsync(cancellationToken: ct))
            .Single(item =>
                item.EventType == SessionEventTypes.RevisionAccepted);
        receipt.CrossSystemRefs.Should().Contain(new Dictionary<string, string>
        {
            ["fromRevision"] = "uninitialized",
            ["toRevision"] = "resource-version-1"
        });

        var losing = () => catalog.UpdateRevisionAsync(
            session.Id, null, "losing-version", ct);
        var conflict = (await losing.Should()
            .ThrowAsync<SessionRevisionConflictException>()).Which;
        conflict.SessionId.Should().Be(session.Id);
        conflict.ExpectedRevision.Should().BeNull();
        conflict.ActualRevision.Should().Be("resource-version-1");
        conflict.ActualVersion.Should().Be(1);
        (await catalog.ListPendingAsync(cancellationToken: ct))
            .Should().NotContain(item =>
                item.CrossSystemRefs.GetValueOrDefault("toRevision") == "losing-version");
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

        static async Task<object> CaptureAsync(Task<Session> attempt)
        {
            try { return await attempt; }
            catch (Exception exception) { return exception; }
        }
        var results = await Task.WhenAll(
            CaptureAsync(first.UpdateRevisionAsync(session.Id, null, "revision-a", ct)),
            CaptureAsync(second.UpdateRevisionAsync(session.Id, null, "revision-b", ct)));

        results.Should().ContainSingle(item => item is Session);
        results.Should().ContainSingle(item => item is SessionRevisionConflictException);
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
        acquired.FencingToken.Should().BeGreaterThan(held.FencingToken);
        acquired.ExpiresAt.Should().BeAfter(acquired.AcquiredAt);
        await acquired.AssertOwnershipAsync(ct);
    }

    [Fact]
    public async Task DecisionLease_LossSignalsAndStaleHolderIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(rootPath, "lease-loss.db");
        var catalog = new SqliteSessionCatalog(
            path,
            pooling: false,
            decisionLeaseDuration: TimeSpan.FromSeconds(2),
            decisionLeaseRenewalInterval: TimeSpan.FromMilliseconds(50));
        var session = await catalog.CreateAsync("context", "resource", cancellationToken: ct);
        var first = await catalog.AcquireAsync(session.Id, Guid.CreateVersion7(), ct);
        await first.AssertOwnershipAsync(ct);

        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM session_decision_leases WHERE session_id = $sessionId;";
            command.Parameters.AddWithValue("$sessionId", session.Id.ToString());
            await command.ExecuteNonQueryAsync(ct);
        }

        var lost = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = first.LeaseLost.Register(() => lost.TrySetResult());
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
        var staleAssertion = () => first.AssertOwnershipAsync(ct);
        await staleAssertion.Should().ThrowAsync<SessionDecisionLeaseLostException>();

        await using var second = await catalog.AcquireAsync(
            session.Id, Guid.CreateVersion7(), ct);
        second.FencingToken.Should().BeGreaterThan(first.FencingToken);
        await second.AssertOwnershipAsync(ct);

        var disposeLost = async () => await first.DisposeAsync();
        await disposeLost.Should().ThrowAsync<SessionDecisionLeaseLostException>();
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
