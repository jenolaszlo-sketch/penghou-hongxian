using Penghou.Siming;

namespace Penghou.Hongxian.Sqlite;

/// <summary>
/// Configures the standard local SQLite composition without replacing any of
/// Hongxian's provider interfaces.
/// </summary>
public sealed record HongxianSqliteOptions
{
    public required string RootPath { get; init; }

    public bool Pooling { get; init; } = true;

    public int MaximumCachedLedgers { get; init; } = 32;

    public LedgerInputLimits LedgerInputLimits { get; init; } =
        LedgerInputLimits.Default;

    public SessionEventTimePolicy EventTimePolicy { get; init; } =
        SessionEventTimePolicy.Default;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public TimeSpan DecisionLeaseDuration { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan DecisionLeaseRenewalInterval { get; init; } =
        TimeSpan.FromSeconds(10);

    public TimeSpan DecisionLeaseRetryDelay { get; init; } =
        TimeSpan.FromMilliseconds(25);

    public SessionParticipantAttribution EvidenceParticipant { get; init; } =
        SessionParticipantAttribution.System("evidence-dispatcher", "hongxian");

    internal string FullRootPath => Path.GetFullPath(RootPath);

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RootPath);
        if (MaximumCachedLedgers < 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumCachedLedgers));
        ArgumentNullException.ThrowIfNull(LedgerInputLimits);
        LedgerInputLimits.Validate();
        ArgumentNullException.ThrowIfNull(EventTimePolicy);
        EventTimePolicy.Validate();
        ArgumentNullException.ThrowIfNull(TimeProvider);
        SessionContractValidation.Validate(
            EvidenceParticipant,
            nameof(EvidenceParticipant));
        if (DecisionLeaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(DecisionLeaseDuration));
        if (DecisionLeaseRenewalInterval <= TimeSpan.Zero ||
            DecisionLeaseRenewalInterval >= DecisionLeaseDuration)
            throw new ArgumentOutOfRangeException(nameof(DecisionLeaseRenewalInterval));
        if (DecisionLeaseRetryDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(DecisionLeaseRetryDelay));
    }
}

/// <summary>
/// Standard SQLite-backed Hongxian stores sharing one root, clock, projection,
/// and evidence-attribution policy. Consumers may use the concrete stores or
/// their provider-neutral interface views.
/// </summary>
public sealed class HongxianSqliteStoreSet : IAsyncDisposable, IDisposable
{
    private int disposed;

    public HongxianSqliteStoreSet(HongxianSqliteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        RootPath = options.FullRootPath;
        Directory.CreateDirectory(RootPath);

        Projections = new SqliteSessionProjectionStore(
            Path.Combine(RootPath, "projections.db"),
            options.TimeProvider,
            options.Pooling);
        Events = new SimingSessionEventStore(
            Path.Combine(RootPath, "sessions"),
            options.LedgerInputLimits,
            Projections,
            options.MaximumCachedLedgers,
            options.EventTimePolicy,
            options.TimeProvider);
        Catalog = new SqliteSessionCatalog(
            Path.Combine(RootPath, "catalog.db"),
            options.TimeProvider,
            options.Pooling,
            options.DecisionLeaseDuration,
            options.DecisionLeaseRenewalInterval,
            options.DecisionLeaseRetryDelay);
        Operations = new SqliteCrossStoreOperationStore(
            Path.Combine(RootPath, "operations.db"),
            options.Pooling);
        CatalogEvidence = new SessionEvidenceOutboxDispatcher(
            Catalog,
            Events,
            options.EvidenceParticipant);
        OperationEvidence = new SessionEvidenceOutboxDispatcher(
            Operations,
            Events,
            options.EvidenceParticipant);
        ConsistencyAudit = new SessionConsistencyAuditService(
            Events,
            Projections,
            Catalog,
            Operations,
            [
                new SessionEvidenceOutboxAuditSource("catalog", Catalog),
                new SessionEvidenceOutboxAuditSource("operations", Operations)
            ],
            Catalog,
            options.TimeProvider);
    }

    public string RootPath { get; }

    public SimingSessionEventStore Events { get; }

    public SqliteSessionProjectionStore Projections { get; }

    public SqliteSessionCatalog Catalog { get; }

    public SqliteCrossStoreOperationStore Operations { get; }

    public SessionEvidenceOutboxDispatcher CatalogEvidence { get; }

    public SessionEvidenceOutboxDispatcher OperationEvidence { get; }

    public SessionConsistencyAuditService ConsistencyAudit { get; }

    public ISessionEventStore EventStore => Events;

    public ISessionEventDeliveryStore EventDeliveryStore => Events;

    public ISessionProjectionStore ProjectionStore => Projections;

    public ISessionProjectionDeliveryStore ProjectionDelivery => Projections;

    public ISessionStore SessionStore => Catalog;

    public ISessionDecisionLeaseProvider DecisionLeases => Catalog;

    public ISessionDecisionLeaseInspector DecisionLeaseInspector => Catalog;

    public ICrossStoreOperationStore OperationStore => Operations;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await Events.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Events.Dispose();
    }
}
