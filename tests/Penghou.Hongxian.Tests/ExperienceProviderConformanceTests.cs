using Penghou.Hongxian.LatticeDb;

namespace Penghou.Hongxian.Tests;

/// <summary>
/// Runs the portable experience-provider conformance cases against every
/// available provider shape: the in-memory reference, LatticeDB memory, and
/// the transport-shaped fake remote. Provider-specific behaviors stay in the
/// provider-owned test classes.
/// </summary>
public sealed class ExperienceProviderConformanceTests
{
    [Fact]
    public Task Replay_IsIdempotentAcrossProviderShapes() =>
        AgainstAllShapesAsync(ExperienceProviderConformanceCases.ReplayRoundtripAsync);

    [Fact]
    public Task LexicalSearch_IsProjectionScopedAcrossProviderShapes() =>
        AgainstAllShapesAsync(ExperienceProviderConformanceCases.LexicalSearchIsProjectionScopedAsync);

    [Fact]
    public Task ConflictingContent_IsRejectedAcrossProviderShapes() =>
        AgainstAllShapesAsync(ExperienceProviderConformanceCases.ConflictingContentIsRejectedAsync);

    [Fact]
    public Task Traversal_ReportsBoundsAcrossProviderShapes() =>
        AgainstAllShapesAsync(ExperienceProviderConformanceCases.TraversalReportsBoundsAsync);

    [Fact]
    public Task DeleteAndReplay_IsEquivalentAcrossProviderShapes() =>
        AgainstAllShapesAsync(ExperienceProviderConformanceCases.DeleteAndReplayIsEquivalentAsync);

    private static async Task AgainstAllShapesAsync(
        Func<
            IExperienceProjectionModelWriter,
            IExperienceRecallReader,
            string,
            CancellationToken,
            Task> run)
    {
        var ct = TestContext.Current.CancellationToken;
        var memory = new InMemoryExperienceProvider();
        await run(memory, memory, "memory", ct);

        using var lattice = new LatticeDbExperienceProvider(
            new LatticeDbExperienceProviderOptions
            {
                Location = LatticeDbLocationKind.Memory,
                Lock = false
            });
        await run(lattice, lattice, "latticedb", ct);

        var remote = new FakeRemoteExperienceProvider();
        await run(remote, remote, "remote-fake", ct);
    }
}
