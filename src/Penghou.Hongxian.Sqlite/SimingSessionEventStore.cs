using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Penghou.Siming;
using Penghou.Siming.Sqlite;

namespace Penghou.Hongxian.Sqlite;

/// <summary>
/// Persists each Hongxian session in its own transactional Siming SQLite ledger
/// under <c>{root}/{session-id}/session.db</c>.
/// </summary>
public sealed class SimingSessionEventStore : ISessionEventStore, IAsyncDisposable, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string rootPath;
    private readonly LedgerInputLimits inputLimits;
    private readonly ISessionProjectionStore? projectionStore;
    private readonly int maximumCachedLedgers;
    private readonly SessionEventTimePolicy timePolicy;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim ledgerGate = new(1, 1);
    private readonly Dictionary<SessionId, CachedLedgerEntry> ledgers = [];
    private int disposed;

    /// <summary>Creates a session event store rooted at the supplied directory.</summary>
    public SimingSessionEventStore(
        string rootPath,
        LedgerInputLimits? inputLimits = null,
        ISessionProjectionStore? projectionStore = null,
        int maximumCachedLedgers = 32,
        SessionEventTimePolicy? timePolicy = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        this.rootPath = Path.GetFullPath(rootPath);
        this.inputLimits = inputLimits ?? LedgerInputLimits.Default;
        this.projectionStore = projectionStore;
        this.timePolicy = timePolicy ?? SessionEventTimePolicy.Default;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (maximumCachedLedgers < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumCachedLedgers));
        this.maximumCachedLedgers = maximumCachedLedgers;
        this.inputLimits.Validate();
        this.timePolicy.Validate();
        Directory.CreateDirectory(this.rootPath);
    }

    /// <inheritdoc />
    public async Task<SessionEvent> AppendAsync(SessionEventRequest request, CancellationToken cancellationToken = default)
    {
        SessionContractValidation.Validate(request);
        var now = timeProvider.GetUtcNow();
        if (request.OccurredAt - now > timePolicy.MaximumFutureSkew)
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.OccurredAt,
                $"Occurrence-time claim cannot be more than {timePolicy.MaximumFutureSkew} in the future.");
        await using var ledgerLease = await AcquireLedgerAsync(
            request.SessionId, cancellationToken).ConfigureAwait(false);
        var ledger = ledgerLease.Ledger;
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existing = await ledger.ReadByIdempotencyKeyAsync(request.IdempotencyKey, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                var replay = ResolveReplay(existing, request);
                await ApplyProjectionAsync(replay, cancellationToken).ConfigureAwait(false);
                return replay;
            }
        }

        var payload = SessionEventPayload.From(request, request.EventId ?? Guid.CreateVersion7());
        try
        {
            var entry = await ledger.AppendAsync(
                new LedgerAppendRequest<SessionEventPayload>(
                    request.SessionId.ToString(),
                    request.EventType,
                    payload,
                    request.IdempotencyKey,
                    request.ExpectedHead is null ? null : Map(request.ExpectedHead)),
                cancellationToken).ConfigureAwait(false);
            var committed = Map(entry);
            await ApplyProjectionAsync(committed, cancellationToken).ConfigureAwait(false);
            return committed;
        }
        catch (LedgerIdempotencyConflictException) when (request.IdempotencyKey is not null)
        {
            var existing = await ledger.ReadByIdempotencyKeyAsync(request.IdempotencyKey, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidOperationException(
                    $"Session event idempotency key '{request.IdempotencyKey}' conflicted but the committed entry could not be read.");
            var replay = ResolveReplay(existing, request);
            await ApplyProjectionAsync(replay, cancellationToken).ConfigureAwait(false);
            return replay;
        }
        catch (Penghou.Siming.LedgerHeadConflictException conflict)
        {
            throw new SessionLedgerHeadConflictException(
                Map(conflict.ExpectedHead),
                Map(conflict.ActualHead),
                conflict);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionEvent>> ReadAsync(SessionId sessionId, long afterSequence = 0, CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        var result = new List<SessionEvent>();
        var cursor = afterSequence;
        while (true)
        {
            var page = await ReadPageAsync(new SessionEventPageRequest(sessionId, cursor, SessionEventPageRequest.MaximumLimit), cancellationToken)
                .ConfigureAwait(false);
            result.AddRange(page.Events);
            if (!page.HasMore) return result;
            cursor = page.NextSequence!.Value;
        }
    }

    /// <inheritdoc />
    public async Task<SessionEventPage> ReadPageAsync(SessionEventPageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var selected = new List<SessionEvent>(request.Limit + 1);
        await using var ledgerLease = await AcquireLedgerAsync(
            request.SessionId, cancellationToken).ConfigureAwait(false);
        await foreach (var entry in ledgerLease.Ledger.ReadAsync(
            new LedgerReadRequest(AfterSequence: request.AfterSequence, Limit: request.Limit + 1), cancellationToken).ConfigureAwait(false))
            selected.Add(Map(entry));
        var hasMore = selected.Count > request.Limit;
        if (hasMore) selected.RemoveAt(selected.Count - 1);
        return new SessionEventPage(selected, hasMore ? selected[^1].Sequence : null, hasMore);
    }

    /// <inheritdoc />
    public async Task<SessionEvent?> VerifyChainAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        var history = await ReadVerifiedHistoryAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        return history.Events.LastOrDefault();
    }

    /// <inheritdoc />
    public async Task<VerifiedSessionHistory> ReadVerifiedHistoryAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        SessionContractValidation.ValidateSessionId(sessionId, nameof(sessionId));
        await using var ledgerLease = await AcquireLedgerAsync(
            sessionId, cancellationToken).ConfigureAwait(false);
        var ledger = ledgerLease.Ledger;
        var verification = await ledger.VerifyAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!verification.IsValid)
            throw new SessionLedgerCorruptionException(
                sessionId,
                verification.FailedSequence,
                verification.Failure?.ToString() ?? "unknown",
                verification.Detail);
        var events = new List<SessionEvent>(checked((int)Math.Min(
            verification.VerifiedHead.Sequence,
            int.MaxValue)));
        var cursor = 0L;
        while (cursor < verification.VerifiedHead.Sequence)
        {
            var pageStart = cursor;
            var remaining = verification.VerifiedHead.Sequence - cursor;
            var limit = (int)Math.Min(remaining, LedgerReadRequest.MaximumLimit);
            await foreach (var entry in ledger.ReadAsync(
                new LedgerReadRequest(AfterSequence: cursor, Limit: limit),
                cancellationToken).ConfigureAwait(false))
            {
                if (entry.Sequence > verification.VerifiedHead.Sequence) break;
                events.Add(Map(entry));
                cursor = entry.Sequence;
            }
            if (cursor == pageStart) break;
        }
        if (cursor != verification.VerifiedHead.Sequence)
            throw new InvalidDataException(
                $"Verified session history ended at sequence {cursor}, expected {verification.VerifiedHead.Sequence}.");
        return new VerifiedSessionHistory(
            sessionId,
            Map(verification.VerifiedHead),
            events);
    }

    /// <summary>Returns the deterministic ledger path for a session.</summary>
    public string GetLedgerPath(SessionId sessionId) =>
        Path.Combine(rootPath, sessionId.ToString(), "session.db");

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>[] created;
        await ledgerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            created = ledgers.Values
                .Where(item => item.Ledger.IsValueCreated)
                .Select(item => item.Ledger.Value)
                .ToArray();
            ledgers.Clear();
        }
        finally
        {
            ledgerGate.Release();
        }
        foreach (var ledger in created)
            await ledger.DisposeAsync().ConfigureAwait(false);
        ledgerGate.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async ValueTask<LedgerLease> AcquireLedgerAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        List<SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>>? evicted = null;
        await ledgerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (!ledgers.TryGetValue(sessionId, out var entry))
            {
                entry = new CachedLedgerEntry(new Lazy<SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>>(
                    () => new SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>(
                        new SimingSqliteOptions
                        {
                            DatabasePath = GetLedgerPath(sessionId),
                            InputLimits = inputLimits
                        },
                        new CanonicalJsonPayloadSerializer(SerializerOptions)),
                    LazyThreadSafetyMode.ExecutionAndPublication));
                ledgers.Add(sessionId, entry);
            }
            entry.ReferenceCount++;
            entry.LastUsed = timeProvider.GetUtcNow();
            evicted = TrimUnlocked(sessionId);
            return new LedgerLease(this, sessionId, entry.Ledger.Value);
        }
        finally
        {
            ledgerGate.Release();
            if (evicted is not null)
                foreach (var ledger in evicted)
                    await ledger.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask ReleaseLedgerAsync(SessionId sessionId)
    {
        List<SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>>? evicted;
        await ledgerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ledgers.TryGetValue(sessionId, out var entry))
                return;
            entry.ReferenceCount--;
            entry.LastUsed = timeProvider.GetUtcNow();
            evicted = TrimUnlocked(default);
        }
        finally
        {
            ledgerGate.Release();
        }
        if (evicted is not null)
            foreach (var ledger in evicted)
                await ledger.DisposeAsync().ConfigureAwait(false);
    }

    private List<SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>>? TrimUnlocked(
        SessionId protectedSession)
    {
        List<SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>>? evicted = null;
        while (ledgers.Count > maximumCachedLedgers)
        {
            var candidate = ledgers
                .Where(pair => pair.Key != protectedSession && pair.Value.ReferenceCount == 0)
                .OrderBy(pair => pair.Value.LastUsed)
                .FirstOrDefault();
            if (candidate.Value is null)
                break;
            ledgers.Remove(candidate.Key);
            if (candidate.Value.Ledger.IsValueCreated)
                (evicted ??= []).Add(candidate.Value.Ledger.Value);
        }
        return evicted;
    }

    private sealed class CachedLedgerEntry(
        Lazy<SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>> ledger)
    {
        public Lazy<SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer>> Ledger { get; } = ledger;
        public int ReferenceCount { get; set; }
        public DateTimeOffset LastUsed { get; set; }
    }

    private sealed class LedgerLease(
        SimingSessionEventStore owner,
        SessionId sessionId,
        SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer> ledger) : IAsyncDisposable
    {
        private int disposed;
        public SqliteAppendOnlyLedger<CanonicalJsonPayloadSerializer> Ledger { get; } = ledger;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref disposed, 1) == 0
                ? owner.ReleaseLedgerAsync(sessionId)
                : ValueTask.CompletedTask;
    }

    private async Task ApplyProjectionAsync(
        SessionEvent sessionEvent,
        CancellationToken cancellationToken)
    {
        if (projectionStore is null)
            return;
        var delivery = projectionStore as ISessionProjectionDeliveryStore;
        if (delivery is not null)
        {
            try
            {
                await delivery.RecordCommittedAsync(sessionEvent, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Trace.TraceError(
                    "Could not record committed projection cursor for session {0} sequence {1}: {2}",
                    sessionEvent.SessionId,
                    sessionEvent.Sequence,
                    exception.Message);
            }
        }

        try
        {
            await projectionStore.ApplyAsync(sessionEvent, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (delivery is not null)
            {
                try
                {
                    await delivery.RecordFailureAsync(
                        sessionEvent,
                        exception,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception statusException)
                {
                    Trace.TraceError(
                        "Could not record projection failure for session {0} sequence {1}: {2}",
                        sessionEvent.SessionId,
                        sessionEvent.Sequence,
                        statusException.Message);
                }
            }
            Trace.TraceError(
                "Session projection delivery lagged for session {0} sequence {1}: {2}",
                sessionEvent.SessionId,
                sessionEvent.Sequence,
                exception.Message);
        }
    }

    private static SessionEvent ResolveReplay(LedgerEntry entry, SessionEventRequest request)
    {
        var existing = Map(entry);
        if (!EquivalentRequest(existing, request))
            throw new SessionEventIdempotencyConflictException(
                request.SessionId,
                request.IdempotencyKey!,
                existing.EventId);
        return existing;
    }

    private static SessionEvent Map(LedgerEntry entry)
    {
        var payload = JsonSerializer.Deserialize<SessionEventPayload>(entry.Payload.Span, SerializerOptions)
            ?? throw new InvalidDataException($"Session event payload at ledger sequence {entry.Sequence} is empty.");
        if (payload.SchemaVersion is < SessionEventEnvelopeSchema.MinimumSupportedVersion or
            > SessionEventEnvelopeSchema.CurrentVersion)
            throw new UnsupportedSessionEventSchemaException(payload.SchemaVersion);
        payload.PayloadSchema?.Validate();
        var participant = payload.SchemaVersion == 1
            ? new SessionParticipantAttribution(
                SessionParticipantKinds.Legacy,
                "hongxian-preview-1",
                payload.Actor ?? throw new InvalidDataException(
                    $"Legacy session event at sequence {entry.Sequence} has no actor."))
            : payload.Participant ?? throw new InvalidDataException(
                $"Session event at sequence {entry.Sequence} has no participant attribution.");
        if (payload.SchemaVersion >= 2)
            SessionContractValidation.Validate(participant, nameof(payload.Participant));
        return new SessionEvent
        {
            SchemaVersion = payload.SchemaVersion,
            Sequence = entry.Sequence,
            EventId = payload.EventId,
            SessionId = SessionId.Parse(entry.StreamId),
            Participant = participant,
            EventType = entry.EventType,
            OccurredAt = payload.OccurredAt,
            CommittedAt = entry.CommittedAt,
            CausationId = payload.CausationId,
            CorrelationId = payload.CorrelationId,
            IdempotencyKey = payload.IdempotencyKey,
            CrossSystemRefs = payload.CrossSystemRefs,
            PayloadJson = payload.PayloadJson,
            Payload = payload.Payload?.Clone(),
            PayloadSchema = payload.PayloadSchema,
            PayloadSensitivity = payload.PayloadSensitivity,
            PayloadRetention = payload.PayloadRetention,
            PayloadDigest = payload.PayloadDigest,
            PreviousHash = entry.Sequence == 1 ? null : entry.PreviousHash.ToString(),
            Hash = entry.Hash.ToString()
        };
    }

    private static SessionLedgerHead Map(LedgerHead head) => new(
        head.LedgerId.Value.ToString("D"),
        head.Sequence,
        head.Hash.ToString());

    private static LedgerHead Map(SessionLedgerHead head)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(head.LedgerIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(head.Hash);
        if (head.Sequence < 0) throw new ArgumentOutOfRangeException(nameof(head));
        return new LedgerHead(
            new LedgerId(Guid.Parse(head.LedgerIdentity)),
            head.Sequence,
            new LedgerHash(Convert.FromHexString(head.Hash)),
            LedgerFormatV1.Version);
    }

    private static bool EquivalentRequest(SessionEvent existing, SessionEventRequest replay) =>
        existing.SessionId == replay.SessionId && EquivalentParticipant(existing, replay.Participant) &&
        existing.EventType == replay.EventType && existing.CausationId == replay.CausationId &&
        existing.CorrelationId == replay.CorrelationId &&
        (replay.EventId is null || existing.EventId == replay.EventId) &&
        existing.PayloadSensitivity == replay.PayloadSensitivity &&
        existing.PayloadRetention == replay.PayloadRetention &&
        existing.PayloadSchema == replay.PayloadSchema &&
        EquivalentReferences(existing.CrossSystemRefs, replay.CrossSystemRefs) &&
        EquivalentPayload(existing, replay);

    private static bool EquivalentParticipant(
        SessionEvent existing,
        SessionParticipantAttribution replay) =>
        existing.SchemaVersion == 1 &&
        existing.Participant.Kind == SessionParticipantKinds.Legacy
            ? string.Equals(
                existing.Participant.Subject,
                replay.Subject,
                StringComparison.Ordinal)
            : existing.Participant == replay;

    private static bool EquivalentPayload(SessionEvent existing, SessionEventRequest replay) =>
        replay.PayloadRetention switch
        {
            SessionPayloadRetention.Retain => EquivalentRetainedPayload(existing, replay),
            SessionPayloadRetention.DigestOnly => existing.PayloadDigest == ComputePayloadDigest(replay),
            SessionPayloadRetention.Omit => existing.PayloadJson is null && existing.Payload is null && existing.PayloadDigest is null,
            _ => false
        };

    private static bool EquivalentRetainedPayload(SessionEvent existing, SessionEventRequest replay)
    {
        if (replay.Payload is { } payload)
            return existing.Payload is { } stored &&
                CanonicalJsonPayloadSerializer.Canonicalize(stored).Span.SequenceEqual(
                    CanonicalJsonPayloadSerializer.Canonicalize(payload).Span);
        return existing.Payload is null && existing.PayloadJson == replay.PayloadJson;
    }

    private static bool EquivalentReferences(IReadOnlyDictionary<string, string>? existing, IReadOnlyDictionary<string, string>? replay)
    {
        if (existing is null || replay is null) return existing is null && replay is null;
        return existing.Count == replay.Count && existing.All(item => replay.TryGetValue(item.Key, out var value) && value == item.Value);
    }

    private sealed record SessionEventPayload(
        int SchemaVersion,
        Guid EventId,
        string? Actor,
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
        JsonElement? Payload = null,
        SessionParticipantAttribution? Participant = null)
    {
        public static SessionEventPayload From(SessionEventRequest request, Guid eventId) =>
            new(SessionEventEnvelopeSchema.CurrentVersion, eventId, null, request.OccurredAt, request.CausationId, request.CorrelationId,
                request.IdempotencyKey, request.CrossSystemRefs,
                request.PayloadRetention == SessionPayloadRetention.Retain ? request.PayloadJson : null,
                request.PayloadSensitivity,
                request.PayloadRetention,
                request.PayloadRetention == SessionPayloadRetention.Omit
                    ? null
                    : ComputePayloadDigest(request),
                request.PayloadSchema,
                request.PayloadRetention == SessionPayloadRetention.Retain
                    ? request.Payload?.Clone()
                    : null,
                request.Participant);
    }

    private static string? ComputePayloadDigest(SessionEventRequest request)
    {
        if (request.Payload is { } payload)
            return $"sha256:penghou-canonical-json:v1:{Convert.ToHexStringLower(
                SHA256.HashData(CanonicalJsonPayloadSerializer.Canonicalize(payload).Span))}";
        return request.PayloadJson is null
            ? null
            : $"sha256:utf8:v1:{Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(request.PayloadJson)))}";
    }
}
