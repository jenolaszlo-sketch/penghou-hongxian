using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;

namespace Penghou.Hongxian.Tests;

/// <summary>
/// A provider-neutral view of the standard composition used by conformance
/// tests. Providers can reuse the contract tests by supplying these same
/// interfaces without changing any assertions.
/// </summary>
internal sealed class ProviderConformanceFixture : IDisposable
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "hongxian-provider-conformance",
        Guid.NewGuid().ToString("N"));
    private readonly HongxianSqliteStoreSet stores;

    public ProviderConformanceFixture()
    {
        stores = new HongxianSqliteStoreSet(new HongxianSqliteOptions
        {
            RootPath = rootPath,
            Pooling = false,
            DecisionLeaseDuration = TimeSpan.FromSeconds(5),
            DecisionLeaseRenewalInterval = TimeSpan.FromMilliseconds(100)
        });
    }

    public ISessionEventStore Events => stores.EventStore;

    public ISessionEventDeliveryStore EventDelivery => stores.EventDeliveryStore;

    public ISessionProjectionStore Projections => stores.ProjectionStore;

    public ISessionProjectionDeliveryStore ProjectionDelivery => stores.ProjectionDelivery;

    public ISessionStore Catalog => stores.SessionStore;

    public ISessionDecisionLeaseProvider Leases => stores.DecisionLeases;

    public ICrossStoreOperationStore Operations => stores.OperationStore;

    public ISessionEvidenceOutbox CatalogOutbox => stores.Catalog;

    public SessionEvidenceOutboxDispatcher CatalogDispatcher => stores.CatalogEvidence;

    public SessionConsistencyAuditService Inspection => stores.ConsistencyAudit;

    public Task<Session> CreateSessionAsync(
        string context = "conformance",
        string resource = "resource",
        CancellationToken cancellationToken = default) =>
        stores.Catalog.CreateAsync(context, resource, cancellationToken: cancellationToken);

    public void Dispose()
    {
        stores.Dispose();
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, recursive: true);
    }
}
