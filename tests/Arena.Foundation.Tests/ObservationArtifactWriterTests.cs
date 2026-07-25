using System.Text.Json;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Orchestrator;
using OpenTtd.ModelArena.Storage;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class ObservationArtifactWriterTests
{
    [Fact]
    public void CreatesEveryRequiredPublicArtifactStreamBeforeTheFirstRecord()
    {
        using TemporaryDirectory directory = new();
        using ObservationArtifactWriter writer = new(new RunPathPolicy(directory.CreateDirectory("run")), "run-0001");

        Assert.All(
            new[]
            {
                writer.ObservationsPath,
                writer.GameEventsPath,
                writer.DecisionsPath,
                writer.ProviderUsagePath,
                writer.ActionsPath,
                writer.MetricsPath,
            },
            path =>
            {
                Assert.True(File.Exists(path));
                Assert.Empty(File.ReadAllText(path));
            });
    }

    [Fact]
    public async Task AppendsCanonicalObservationAndEventRecordsInsideTheRunRoot()
    {
        using TemporaryDirectory directory = new();
        string runDirectory = directory.CreateDirectory("run");
        ObservationBuildResult build = ObservationTestData.BuildSnapshot();
        using ObservationArtifactWriter writer = new(new RunPathPolicy(runDirectory), "run-0001");

        await writer.AppendObservationAsync(new ObservationBuildRecord(build.Snapshot, build.Sha256, build.ReplaySha256), CancellationToken.None);
        await writer.AppendEventAsync(build.Snapshot.Sections.RecentEvents[0], CancellationToken.None);

        string observationLine = Assert.Single(File.ReadAllLines(writer.ObservationsPath));
        string eventLine = Assert.Single(File.ReadAllLines(writer.GameEventsPath));
        using JsonDocument observationDocument = JsonDocument.Parse(observationLine);
        using JsonDocument eventDocument = JsonDocument.Parse(eventLine);

        Assert.Equal("run-0001", observationDocument.RootElement.GetProperty("run_id").GetString());
        Assert.Equal(build.Sha256, observationDocument.RootElement.GetProperty("observation_sha256").GetString());
        Assert.Equal("event-0002", eventDocument.RootElement.GetProperty("event").GetProperty("event_id").GetString());
        Assert.Equal(observationLine, CanonicalJson.SerializeToString(observationDocument.RootElement));
        Assert.Equal(eventLine, CanonicalJson.SerializeToString(eventDocument.RootElement));
    }

    [Fact]
    public async Task PersistsEachAuthoritativeEventIdOnlyOnceAcrossRepeatedSnapshots()
    {
        using TemporaryDirectory directory = new();
        ObservationBuildResult build = ObservationTestData.BuildSnapshot();
        using ObservationArtifactWriter writer = new(new RunPathPolicy(directory.CreateDirectory("run")), "run-0001");

        NormalizedGameEvent eventEntry = build.Snapshot.Sections.RecentEvents[0];
        await writer.AppendEventAsync(eventEntry, CancellationToken.None);
        await writer.AppendEventAsync(eventEntry, CancellationToken.None);

        Assert.Single(File.ReadAllLines(writer.GameEventsPath));
    }

    [Fact]
    public async Task RejectsAnObservationForAnotherRun()
    {
        using TemporaryDirectory directory = new();
        using ObservationArtifactWriter writer = new(new RunPathPolicy(directory.CreateDirectory("run")), "run-0001");
        ObservationBuildResult build = ObservationTestData.BuildSnapshot();

        ObservationSnapshot foreignSnapshot = build.Snapshot with { RunId = "run-0002" };

        await Assert.ThrowsAsync<ArgumentException>(() => writer.AppendObservationAsync(
            new ObservationBuildRecord(foreignSnapshot, build.Sha256, build.ReplaySha256),
            CancellationToken.None));
    }
}
