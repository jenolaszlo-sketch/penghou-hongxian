namespace Penghou.Hongxian.Tests;

internal sealed class InMemoryExperienceProjectionCheckpointStore :
    IExperienceProjectionCheckpointStore,
    IExperienceProjectionCheckpointResetStore
{
    private readonly object gate = new();
    private readonly Dictionary<ExperienceProjectionId, ExperienceProjectionCheckpoint> checkpoints = [];

    public Task<ExperienceProjectionCheckpoint?> GetAsync(
        ExperienceProjectionId projectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (projectionId.Value == Guid.Empty)
            throw new ArgumentException(
                "A non-empty experience projection ID is required.",
                nameof(projectionId));
        lock (gate) return Task.FromResult(checkpoints.GetValueOrDefault(projectionId));
    }

    public Task<ExperienceProjectionCheckpoint> AdvanceAsync(AdvanceExperienceProjectionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            checkpoints.TryGetValue(request.Descriptor.ProjectionId, out var current);
            var next = ExperienceProjectionCheckpointRules.Validate(current, request);
            checkpoints[request.Descriptor.ProjectionId] = next;
            return Task.FromResult(next);
        }
    }

    public Task ResetAsync(
        ExperienceProjectionId projectionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) checkpoints.Remove(projectionId);
        return Task.CompletedTask;
    }
}
