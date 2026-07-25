using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Orchestrator;
using OpenTtd.ModelArena.Storage;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class ObservationReplayReaderTests
{
    [Fact]
    public async Task ReadsAndVerifiesPublicObservationArtifactsWithoutAProvider()
    {
        using TemporaryDirectory directory = new();
        string runDirectory = directory.CreateDirectory("run");
        ObservationBuildResult observation = ObservationTestData.BuildSnapshot();
        using (ObservationArtifactWriter writer = new(new RunPathPolicy(runDirectory), "run-0001"))
        {
            await writer.AppendObservationAsync(
                new ObservationBuildRecord(observation.Snapshot, observation.Sha256, observation.ReplaySha256),
                CancellationToken.None);
        }

        ObservationReplayResult result = await ObservationReplayReader.ReadAsync(
            Path.Combine(runDirectory, ObservationArtifactWriter.ObservationsFileName),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        ObservationReplayFrame frame = Assert.Single(result.Frames);
        Assert.Equal("run-0001", frame.RunId);
        Assert.Equal(observation.Sha256, frame.ObservationSha256);
        Assert.Equal(100_000, frame.Cash);
        Assert.Equal("opportunity-1-2-passengers", frame.TopOpportunityId);
    }

    [Fact]
    public async Task RejectsATamperedCanonicalObservationHash()
    {
        using TemporaryDirectory directory = new();
        string runDirectory = directory.CreateDirectory("run");
        ObservationBuildResult observation = ObservationTestData.BuildSnapshot();
        string path;
        using (ObservationArtifactWriter writer = new(new RunPathPolicy(runDirectory), "run-0001"))
        {
            await writer.AppendObservationAsync(
                new ObservationBuildRecord(observation.Snapshot, observation.Sha256, observation.ReplaySha256),
                CancellationToken.None);
            path = writer.ObservationsPath;
        }

        string original = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, original.Replace(observation.Sha256, new string('f', 64), StringComparison.Ordinal));

        ObservationReplayResult result = await ObservationReplayReader.ReadAsync(path, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ArenaErrorCodes.ArtifactVerificationFailed, result.ErrorCode);
    }
}
