using FluentAssertions;
using Microsoft.Data.Sqlite;
using Penghou.Hongxian.Sqlite;

namespace Penghou.Hongxian.Tests;

public sealed class HongxianSqliteStoreSetTests : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "hongxian-store-set-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StandardComposition_WiresStoresProjectionAndEvidence()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var stores = new HongxianSqliteStoreSet(new HongxianSqliteOptions
        {
            RootPath = rootPath,
            Pooling = false,
            MaximumCachedLedgers = 2
        });

        var session = await stores.SessionStore.CreateAsync(
            "example",
            "resource/1",
            cancellationToken: ct);
        var dispatch = await stores.CatalogEvidence.DispatchPendingAsync(
            cancellationToken: ct);
        var projection = await stores.ProjectionStore.GetAsync(session.Id, ct);

        dispatch.Should().Be(new SessionEvidenceDispatchResult(1, 1));
        projection!.AppliedSequence.Should().Be(1);
        (await stores.EventStore.VerifyChainAsync(session.Id, ct)).Should().NotBeNull();
        stores.DecisionLeases.Should().BeSameAs(stores.Catalog);
        stores.OperationStore.Should().BeSameAs(stores.Operations);
    }

    [Fact]
    public void Options_RejectInconsistentLeaseTiming()
    {
        var create = () => new HongxianSqliteStoreSet(new HongxianSqliteOptions
        {
            RootPath = rootPath,
            DecisionLeaseDuration = TimeSpan.FromSeconds(5),
            DecisionLeaseRenewalInterval = TimeSpan.FromSeconds(5)
        });

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
    }
}
