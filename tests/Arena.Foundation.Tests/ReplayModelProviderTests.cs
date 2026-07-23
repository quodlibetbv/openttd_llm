using System.Text.Json;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Providers;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class ReplayModelProviderTests
{
    [Fact]
    public void ReadsTheSanitizedReplayFixtureFormat()
    {
        string fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "providers",
            "replay-decision.v1.json");

        using FileStream stream = File.OpenRead(fixturePath);
        ReplayFixture fixture = ReplayFixtureReader.Read(stream);

        Assert.Equal("1.0", fixture.FixtureVersion);
        Assert.Single(fixture.Steps);
        Assert.Equal("decision-0001", fixture.Steps[0].Decision.DecisionId);
    }

    [Fact]
    public async Task ReturnsTheRecordedDecisionForTheMatchingObservation()
    {
        ReplayModelProvider provider = new(CreateFixture());

        ProviderDecisionResult result = await provider.GetDecisionAsync(
            CreateRequest("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("decision-0001", result.Decision?.DecisionId);
        Assert.Equal(0, result.Usage.InputTokens);
    }

    [Fact]
    public async Task RejectsAMismatchedObservationBeforeEmittingADecision()
    {
        ReplayModelProvider provider = new(CreateFixture());

        ProviderDecisionResult result = await provider.GetDecisionAsync(
            CreateRequest("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ArenaErrorCodes.ProviderReplayObservationMismatch, result.Error?.Code);
        Assert.Null(result.Decision);
    }

    [Fact]
    public async Task DoesNotReuseAStepAfterTheFixtureIsExhausted()
    {
        ReplayModelProvider provider = new(CreateFixture());
        ModelRequest request = CreateRequest("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        _ = await provider.GetDecisionAsync(request, CancellationToken.None);
        ProviderDecisionResult exhausted = await provider.GetDecisionAsync(request, CancellationToken.None);

        Assert.False(exhausted.IsSuccess);
        Assert.Equal(ArenaErrorCodes.ProviderReplayExhausted, exhausted.Error?.Code);
    }

    private static ReplayFixture CreateFixture()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "game_days": 30
            }
            """);

        return new ReplayFixture
        {
            FixtureVersion = "1.0",
            Provider = "replay",
            Model = "test-fixture",
            Steps =
            [
                new ReplayStep
                {
                    ExpectedObservationSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                    Decision = new ModelDecision
                    {
                        DecisionId = "decision-0001",
                        PublicSummary = "Wait for the next review interval.",
                        Observations = ["The fixture is deterministic."],
                        Actions =
                        [
                            new ModelAction
                            {
                                Tool = "wait",
                                Arguments = document.RootElement.Clone(),
                            },
                        ],
                        NextReviewGameDays = 30,
                    },
                    Usage = new ReplayUsage
                    {
                        InputTokens = 0,
                        OutputTokens = 0,
                        LatencyMilliseconds = 0,
                    },
                },
            ],
        };
    }

    private static ModelRequest CreateRequest(string observationHash)
    {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "schema_version": "1.0"
            }
            """);

        return new ModelRequest
        {
            RunId = "run-0001",
            DecisionId = "decision-0001",
            ObservationHash = observationHash,
            Observation = document.RootElement.Clone(),
            AvailableTools = ["wait"],
            RemainingModelCalls = 1,
            RemainingOutputTokens = 100,
        };
    }
}
