using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Orchestrator;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class ObservationDeltaCodecTests
{
    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [Fact]
    public void ProducesAnEmptyDeltaForTheSameCanonicalObservation()
    {
        ObservationBuildResult observation = ObservationTestData.BuildSnapshot();

        ObservationDelta delta = ObservationDeltaCodec.Create(
            observation.Snapshot.RunId,
            observation.CanonicalJson,
            observation.CanonicalJson);

        Assert.Empty(delta.Operations);
        Assert.Equal(observation.Sha256, delta.FromObservationSha256);
        Assert.Equal(observation.Sha256, delta.ToObservationSha256);
    }

    [Fact]
    public void AppliesDeterministicChangesAndVerifiesTheTargetHash()
    {
        ObservationBuildResult previous = ObservationTestData.BuildSnapshot();
        GameScriptSnapshot changedGameState = ObservationTestData.CreateGameSnapshot() with
        {
            GameDate = "1950-02-01",
            GameTick = 73,
            Company = ObservationTestData.CreateGameSnapshot().Company with { Cash = 84_000 },
            Events =
            [
                new NormalizedGameEvent
                {
                    EventId = "event-0003",
                    EventCode = "ARENA-ROUTE-OPERATING",
                    GameDate = "1950-02-01",
                    EntityIds = ["route-4-5", "vehicle-9"],
                    PublicSummary = "The route resumed after a persisted load.",
                    CorrelationId = "correlation-0001",
                },
            ],
        };
        ObservationBuildResult current = ObservationBuilder.Build(changedGameState, ObservationTestData.CreateContext());

        ObservationDelta delta = ObservationDeltaCodec.Create(previous.Snapshot.RunId, previous.CanonicalJson, current.CanonicalJson);
        ObservationDeltaApplyResult applied = ObservationDeltaCodec.Apply(previous.CanonicalJson, delta);

        Assert.True(applied.Succeeded);
        Assert.NotNull(applied.Observation);
        Assert.Equal(current.Sha256, ObservationDeltaCodec.ComputeHash(applied.Observation!.Value));
        Assert.Contains(delta.Operations, operation => operation.Path == "/game_date");
        Assert.Contains(delta.Operations, operation => operation.Path == "/sections/financial_summary/cash");
    }

    [Fact]
    public void RejectsADeltaForAnotherBaseSnapshot()
    {
        ObservationBuildResult previous = ObservationTestData.BuildSnapshot();
        ObservationBuildResult changed = ObservationBuilder.Build(
            ObservationTestData.CreateGameSnapshot() with { GameDate = "1950-02-01" },
            ObservationTestData.CreateContext());
        ObservationDelta delta = ObservationDeltaCodec.Create(previous.Snapshot.RunId, previous.CanonicalJson, changed.CanonicalJson);
        using JsonDocument wrongBase = JsonDocument.Parse("{\"schema_version\":\"1.0\"}");

        ObservationDeltaApplyResult applied = ObservationDeltaCodec.Apply(wrongBase.RootElement, delta);

        Assert.False(applied.Succeeded);
        Assert.Equal(ArenaErrorCodes.ArtifactVerificationFailed, applied.Error?.Code);
    }

    [Fact]
    public void SnapshotRoundTripKeepsStableIdsAndProducesNoSyntheticEventDelta()
    {
        GameScriptSnapshot original = ObservationTestData.CreateGameSnapshot();
        JsonElement serialized = JsonSerializer.SerializeToElement(original, ObservationJsonContext.Default.GameScriptSnapshot);
        GameScriptSnapshot restored = JsonSerializer.Deserialize<GameScriptSnapshot>(serialized.GetRawText(), StrictJsonOptions)!;
        ObservationBuildResult before = ObservationBuilder.Build(original, ObservationTestData.CreateContext());
        ObservationBuildResult after = ObservationBuilder.Build(restored, ObservationTestData.CreateContext());

        ObservationDelta delta = ObservationDeltaCodec.Create(before.Snapshot.RunId, before.CanonicalJson, after.CanonicalJson);

        Assert.Equal(before.Sha256, after.Sha256);
        Assert.Empty(delta.Operations);
        Assert.Equal(original.Events.Select(entry => entry.EventId), restored.Events.Select(entry => entry.EventId));
        Assert.Equal(original.Routes.Select(route => route.RouteId), restored.Routes.Select(route => route.RouteId));
    }
}
