namespace Penghou.Hongxian;

/// <summary>One deterministic, provider-neutral effect of a projected event.</summary>
public sealed record ExperienceProjectionEffect(
    ExperienceProjectionId ProjectionId,
    SessionId SessionId,
    long Sequence,
    Guid EventId,
    string Hash,
    string EventType);

/// <summary>
/// The minimal write surface used by a verified projector. Implementations
/// must make ApplyAsync idempotent for the tuple (projection, session, event).
/// A writer may commit an effect before the checkpoint store advances; replay
/// is therefore the recovery mechanism for a non-atomic write.
/// </summary>
public interface IExperienceProjectionWriter
{
    Task<ExperienceProjectionWriteOutcome> ApplyAsync(
        ExperienceProjectionDescriptor descriptor,
        SessionEvent sessionEvent,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        ExperienceProjectionDescriptor descriptor,
        CancellationToken cancellationToken = default);

}

public enum ExperienceProjectionWriteOutcome
{
    Applied,
    AlreadyPresent
}

/// <summary>
/// Raised when a projection effect identity already exists with different
/// content. Treating this as an idempotent replay would hide corruption.
/// </summary>
public sealed class ExperienceProjectionEffectConflictException : Exception
{
    public ExperienceProjectionEffectConflictException(
        ExperienceProjectionEffect existing,
        ExperienceProjectionEffect attempted)
        : base(
            $"Projection effect '{attempted.ProjectionId}/{attempted.SessionId}/" +
            $"{attempted.EventId:D}' already exists with different content.")
    {
        Existing = existing ?? throw new ArgumentNullException(nameof(existing));
        Attempted = attempted ?? throw new ArgumentNullException(nameof(attempted));
    }

    public ExperienceProjectionEffect Existing { get; }

    public ExperienceProjectionEffect Attempted { get; }
}

/// <summary>Optional checkpoint capability required for a complete rebuild.</summary>
public interface IExperienceProjectionCheckpointResetStore
{
    Task ResetAsync(
        ExperienceProjectionId projectionId,
        CancellationToken cancellationToken = default);
}

public enum ExperienceProjectionExecutionFailure
{
    InvalidHistory,
    UnsupportedHistory,
    ProjectionWrite,
    CheckpointCommit,
    CheckpointReset
}

/// <summary>Raised when a verified-history input cannot be projected safely.</summary>
public sealed class ExperienceProjectionExecutionException : Exception
{
    public ExperienceProjectionExecutionException(
        ExperienceProjectionExecutionFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public ExperienceProjectionExecutionFailure Failure { get; }

    /// <summary>
    /// True when a retry must assume that one or more projection effects were
    /// committed. The writer contract makes that retry safe and idempotent.
    /// </summary>
    public bool ProjectionWritesMayHaveCommitted { get; init; }

    public ExperienceProjectionPosition? IntendedPosition { get; init; }
}

/// <summary>Outcome of one forward projection attempt.</summary>
public sealed record ExperienceProjectionExecutionResult(
    ExperienceProjectionCheckpoint Checkpoint,
    int AppliedEventCount,
    int AlreadyPresentEventCount,
    int CheckpointCoveredEventCount,
    bool CheckpointAdvanced)
{
    public int EventCount => AppliedEventCount + AlreadyPresentEventCount +
        CheckpointCoveredEventCount;
}

/// <summary>
/// Projects only complete, independently verified Siming histories. The
/// projector is intentionally storage-neutral: effects and checkpoints are
/// separate ports, so a provider can use its own transaction model.
/// </summary>
public sealed class ExperienceProjectionProjector
{
    private readonly ExperienceProjectionDescriptor descriptor;
    private readonly IExperienceProjectionWriter writer;
    private readonly IExperienceProjectionCheckpointStore checkpoints;

    public ExperienceProjectionProjector(
        ExperienceProjectionDescriptor descriptor,
        IExperienceProjectionWriter writer,
        IExperienceProjectionCheckpointStore checkpoints)
    {
        this.descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        this.checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
    }

    public ExperienceProjectionDescriptor Descriptor => descriptor;

    public Task<ExperienceProjectionExecutionResult> ProjectAsync(
        VerifiedSessionHistory history,
        CancellationToken cancellationToken = default) =>
        ProjectAsync([history], cancellationToken);

    public async Task<ExperienceProjectionExecutionResult> ProjectAsync(
        IReadOnlyList<VerifiedSessionHistory> histories,
        CancellationToken cancellationToken = default)
    {
        var normalized = ValidateHistories(histories);
        cancellationToken.ThrowIfCancellationRequested();
        var current = await checkpoints.GetAsync(
            descriptor.ProjectionId,
            cancellationToken).ConfigureAwait(false);
        var targetPosition = new ExperienceProjectionPosition(
            normalized.Select(history => new SessionEvidencePosition(
                history.SessionId,
                history.VerifiedHead)));

        // Validate advancement before writing. This catches stale/replaced
        // streams without leaving a new provider effect behind.
        if (current is not null)
            ExperienceProjectionCheckpointRules.Validate(
                current,
                new AdvanceExperienceProjectionRequest(
                    descriptor,
                    targetPosition,
                    current.Version));

        var currentBySession = current?.Position.Streams
            .ToDictionary(item => item.SessionId);
        var applied = 0;
        var alreadyPresent = 0;
        var checkpointCovered = 0;
        var writeAttempts = 0;
        try
        {
            foreach (var history in normalized)
            {
                var covered = currentBySession?.GetValueOrDefault(history.SessionId)
                    ?.SessionLedgerHead.Sequence ?? 0;
                foreach (var sessionEvent in history.Events.OrderBy(item => item.Sequence))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (sessionEvent.Sequence <= covered)
                    {
                        checkpointCovered++;
                        continue;
                    }

                    writeAttempts++;
                    var outcome = await writer.ApplyAsync(
                        descriptor,
                        sessionEvent,
                        cancellationToken).ConfigureAwait(false);
                    switch (outcome)
                    {
                        case ExperienceProjectionWriteOutcome.Applied:
                            applied++;
                            break;
                        case ExperienceProjectionWriteOutcome.AlreadyPresent:
                            alreadyPresent++;
                            break;
                        default:
                            throw new InvalidOperationException(
                                $"Unknown projection write outcome '{outcome}'.");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ExperienceProjectionExecutionException(
                ExperienceProjectionExecutionFailure.ProjectionWrite,
                "A projection effect could not be applied. Retry from the " +
                "last checkpoint; already written effects are replay-safe.",
                exception)
            {
                ProjectionWritesMayHaveCommitted = writeAttempts > 0,
                IntendedPosition = targetPosition
            };
        }

        var expectedVersion = current?.Version ?? 0;
        ExperienceProjectionCheckpoint checkpoint;
        try
        {
            checkpoint = await checkpoints.AdvanceAsync(
                new AdvanceExperienceProjectionRequest(
                    descriptor,
                    targetPosition,
                    expectedVersion),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ExperienceProjectionExecutionException(
                ExperienceProjectionExecutionFailure.CheckpointCommit,
                "Projection effects may have committed, but the checkpoint " +
                "did not. Retry is required and is safe because effects are " +
                "idempotent.",
                exception)
            {
                ProjectionWritesMayHaveCommitted = writeAttempts > 0,
                IntendedPosition = targetPosition
            };
        }

        return new ExperienceProjectionExecutionResult(
            checkpoint,
            applied,
            alreadyPresent,
            checkpointCovered,
            !ReferenceEquals(checkpoint, current) && checkpoint.Version != expectedVersion);
    }

    /// <summary>
    /// Drops derived effects and their checkpoint, then replays the supplied
    /// verified heads. Resetting the checkpoint first makes interruption safe:
    /// an incomplete delete is still repaired by idempotent replay.
    /// </summary>
    public async Task<ExperienceProjectionExecutionResult> RebuildAsync(
        IReadOnlyList<VerifiedSessionHistory> histories,
        CancellationToken cancellationToken = default)
    {
        if (checkpoints is not IExperienceProjectionCheckpointResetStore resetStore)
            throw new ExperienceProjectionExecutionException(
                ExperienceProjectionExecutionFailure.CheckpointReset,
                "Complete rebuild requires a checkpoint store that supports reset.");

        var normalized = ValidateHistories(histories);
        var intendedPosition = new ExperienceProjectionPosition(
            normalized.Select(history => new SessionEvidencePosition(
                history.SessionId,
                history.VerifiedHead)));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await resetStore.ResetAsync(
                descriptor.ProjectionId,
                cancellationToken).ConfigureAwait(false);
            await writer.DeleteAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ExperienceProjectionExecutionException(
                ExperienceProjectionExecutionFailure.CheckpointReset,
                "The disposable projection could not be reset completely. " +
                "Retry the complete rebuild; provider state may already have changed.",
                exception)
            {
                ProjectionWritesMayHaveCommitted = true,
                IntendedPosition = intendedPosition
            };
        }

        return await ProjectAsync(histories, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<VerifiedSessionHistory> ValidateHistories(
        IReadOnlyList<VerifiedSessionHistory> histories)
    {
        ArgumentNullException.ThrowIfNull(histories);
        if (histories.Count == 0)
            throw new ExperienceProjectionExecutionException(
                ExperienceProjectionExecutionFailure.InvalidHistory,
                "At least one verified session history is required.");
        if (histories.Count > SessionContractLimits.ProjectionStreamCount)
            throw new ExperienceProjectionExecutionException(
                ExperienceProjectionExecutionFailure.InvalidHistory,
                $"A projection cannot consume more than " +
                $"{SessionContractLimits.ProjectionStreamCount} histories.");

        var normalized = histories
            .Select(ValidateHistory)
            .OrderBy(history => history.SessionId.ToString(), StringComparer.Ordinal)
            .ToArray();
        if (normalized.Select(history => history.SessionId).Distinct().Count() != normalized.Length)
            throw new ExperienceProjectionExecutionException(
                ExperienceProjectionExecutionFailure.InvalidHistory,
                "A projection cannot consume duplicate session histories.");
        return normalized;
    }

    private static VerifiedSessionHistory ValidateHistory(VerifiedSessionHistory? history)
    {
        if (history is null)
            throw new ExperienceProjectionExecutionException(
                ExperienceProjectionExecutionFailure.InvalidHistory,
                "A verified session history is required.");
        try
        {
            SessionContractValidation.ValidateSessionId(history.SessionId, nameof(history));
            ArgumentNullException.ThrowIfNull(history.VerifiedHead);
            if (string.IsNullOrWhiteSpace(history.VerifiedHead.LedgerIdentity) ||
                history.VerifiedHead.LedgerIdentity.Length > SessionContractLimits.LedgerIdentityCharacters ||
                string.IsNullOrWhiteSpace(history.VerifiedHead.Hash) ||
                history.VerifiedHead.Hash.Length > SessionContractLimits.DigestCharacters ||
                history.VerifiedHead.Sequence < 0)
                throw new ArgumentException("The verified head is invalid.");
            ArgumentNullException.ThrowIfNull(history.Events);

            var ordered = history.Events.OrderBy(item => item.Sequence).ToArray();
            if (ordered.LongLength != history.VerifiedHead.Sequence)
                throw new ExperienceProjectionExecutionException(
                    ExperienceProjectionExecutionFailure.InvalidHistory,
                    $"Verified history for '{history.SessionId}' has a gap or " +
                    "does not reach its captured head.");
            if (ordered.Select(item => item?.EventId).Distinct().Count() != ordered.Length)
                throw new ExperienceProjectionExecutionException(
                    ExperienceProjectionExecutionFailure.InvalidHistory,
                    $"Verified history for '{history.SessionId}' contains duplicate event IDs.");
            for (var index = 0; index < ordered.Length; index++)
            {
                var sessionEvent = ordered[index];
                if (sessionEvent is null || sessionEvent.SessionId != history.SessionId ||
                    sessionEvent.Sequence != index + 1)
                    throw new ExperienceProjectionExecutionException(
                        ExperienceProjectionExecutionFailure.InvalidHistory,
                        $"Verified history for '{history.SessionId}' is not contiguous.");
                SessionContractValidation.Validate(sessionEvent);
                var expectedPrevious = index == 0 ? null : ordered[index - 1].Hash;
                if (!string.Equals(sessionEvent.PreviousHash, expectedPrevious, StringComparison.Ordinal))
                    throw new ExperienceProjectionExecutionException(
                        ExperienceProjectionExecutionFailure.InvalidHistory,
                        $"Verified history for '{history.SessionId}' has a broken hash chain.");
            }
            if (ordered.Length > 0 && !string.Equals(
                    ordered[^1].Hash,
                    history.VerifiedHead.Hash,
                    StringComparison.Ordinal))
                throw new ExperienceProjectionExecutionException(
                    ExperienceProjectionExecutionFailure.InvalidHistory,
                    $"Verified history for '{history.SessionId}' does not match its captured head.");

            return new VerifiedSessionHistory(history.SessionId, history.VerifiedHead, ordered);
        }
        catch (ExperienceProjectionExecutionException)
        {
            throw;
        }
        catch (UnsupportedSessionEventSchemaException exception)
        {
            throw new ExperienceProjectionExecutionException(
                ExperienceProjectionExecutionFailure.UnsupportedHistory,
                $"Verified history for '{history.SessionId}' uses an unsupported " +
                "event envelope schema.",
                exception);
        }
        catch (Exception exception)
        {
            throw new ExperienceProjectionExecutionException(
                ExperienceProjectionExecutionFailure.InvalidHistory,
                $"Verified history for '{history.SessionId}' is invalid.",
                exception);
        }
    }
}

/// <summary>
/// A small deterministic in-memory writer used by conformance and rebuild
/// tests. It models the provider behavior required by the projector without
/// becoming part of the portable experience-query contract.
/// </summary>
public sealed class InMemoryExperienceProjectionWriter : IExperienceProjectionWriter
{
    private readonly object gate = new();
    private readonly Dictionary<
        (ExperienceProjectionId ProjectionId, SessionId SessionId, Guid EventId),
        ExperienceProjectionEffect> effects = [];

    public Task<ExperienceProjectionWriteOutcome> ApplyAsync(
        ExperienceProjectionDescriptor descriptor,
        SessionEvent sessionEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(sessionEvent);
        cancellationToken.ThrowIfCancellationRequested();
        SessionContractValidation.Validate(sessionEvent);
        if (sessionEvent.SessionId.Value == Guid.Empty)
            throw new ArgumentException("A non-empty session ID is required.", nameof(sessionEvent));
        var effect = new ExperienceProjectionEffect(
            descriptor.ProjectionId,
            sessionEvent.SessionId,
            sessionEvent.Sequence,
            sessionEvent.EventId,
            sessionEvent.Hash,
            sessionEvent.EventType);
        lock (gate)
        {
            var key = (effect.ProjectionId, effect.SessionId, effect.EventId);
            if (!effects.TryGetValue(key, out var existing))
            {
                effects.Add(key, effect);
                return Task.FromResult(ExperienceProjectionWriteOutcome.Applied);
            }
            if (existing != effect)
                throw new ExperienceProjectionEffectConflictException(existing, effect);
            return Task.FromResult(ExperienceProjectionWriteOutcome.AlreadyPresent);
        }
    }

    public Task DeleteAsync(
        ExperienceProjectionDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            foreach (var key in effects.Keys
                         .Where(key => key.ProjectionId == descriptor.ProjectionId)
                         .ToArray())
                effects.Remove(key);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ExperienceProjectionEffect>> ListAsync(
        ExperienceProjectionId projectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            IReadOnlyList<ExperienceProjectionEffect> result = effects.Values
                .Where(effect => effect.ProjectionId == projectionId)
                .OrderBy(effect => effect.SessionId.ToString(), StringComparer.Ordinal)
                .ThenBy(effect => effect.Sequence)
                .ToArray();
            return Task.FromResult(result);
        }
    }
}
