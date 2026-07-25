using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Orchestrator;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class ObservationBuilderTests
{
    [Fact]
    public void ProducesByteStableCanonicalJsonFromEquivalentUnorderedGameState()
    {
        IReadOnlyList<GameTownState> orderedTowns =
        [
            ObservationTestData.Town(3, "Gamma", 800, 15, 18),
            ObservationTestData.Town(1, "Alpha", 2_000, 10, 10),
            ObservationTestData.Town(2, "Beta", 1_500, 20, 10),
        ];
        IReadOnlyList<NormalizedGameEvent> unorderedEvents =
        [
            new NormalizedGameEvent
            {
                EventId = "event-0002",
                EventCode = "ARENA-ROUTE-OPERATING",
                GameDate = "1950-01-01",
                EntityIds = ["vehicle-9", "route-4-5"],
                PublicSummary = "Route <operating>\u0001",
                CorrelationId = "correlation-0001",
            },
            new NormalizedGameEvent
            {
                EventId = "event-0001",
                EventCode = "ARENA-PROJECT-STARTED",
                GameDate = "1949-12-31",
                EntityIds = ["project-0001"],
                PublicSummary = "Project started.",
            },
        ];

        ObservationBuildResult first = ObservationBuilder.Build(
            ObservationTestData.CreateGameSnapshot(orderedTowns, unorderedEvents),
            ObservationTestData.CreateContext());
        ObservationBuildResult second = ObservationBuilder.Build(
            ObservationTestData.CreateGameSnapshot(orderedTowns.Reverse().ToArray(), unorderedEvents.Reverse().ToArray()),
            ObservationTestData.CreateContext());

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(CanonicalJson.SerializeToString(first.CanonicalJson), CanonicalJson.SerializeToString(second.CanonicalJson));
        Assert.Equal([1, 2, 3], first.Snapshot.Sections.CandidateOpportunities.Towns.Select(town => town.TownId));
        Assert.Equal("event-0001", first.Snapshot.Sections.RecentEvents[0].EventId);
        Assert.Equal("Route ‹operating›", first.Snapshot.Sections.RecentEvents[1].PublicSummary);
        Assert.Equal(["route-4-5", "vehicle-9"], first.Snapshot.Sections.RecentEvents[1].EntityIds);
        Assert.Equal("action-0001", Assert.Single(first.Snapshot.Sections.NetworkSummary.Routes).ActionId);
        Assert.Equal("ARENA-ACTION-PATH-NOT-FOUND", Assert.Single(first.Snapshot.Sections.ActiveProjects).FailureCode);
    }

    [Fact]
    public void ReplayFingerprintNormalizesStartupPopulationAndRankingJitter()
    {
        GameScriptSnapshot firstState = ObservationTestData.CreateGameSnapshot(
        [
            ObservationTestData.Town(1, "Alpha", 2_000, 10, 10),
            ObservationTestData.Town(2, "Beta", 1_500, 20, 10),
            ObservationTestData.Town(3, "Gamma", 800, 15, 18),
        ]);
        GameScriptSnapshot secondState = firstState with
        {
            GameDate = "1950-01-03",
            GameTick = 190,
            Towns =
            [
                ObservationTestData.Town(1, "Alpha", 2_010, 10, 10),
                ObservationTestData.Town(2, "Beta", 1_490, 20, 10),
                ObservationTestData.Town(3, "Gamma", 810, 15, 18),
            ],
        };

        ObservationBuildResult first = ObservationBuilder.Build(firstState, ObservationTestData.CreateContext());
        ObservationBuildResult second = ObservationBuilder.Build(secondState, ObservationTestData.CreateContext());

        Assert.NotEqual(first.Sha256, second.Sha256);
        Assert.Equal(first.ReplaySha256, second.ReplaySha256);
    }

    [Fact]
    public void RanksCandidateTownPairsByDeclaredPopulationThenDistanceRule()
    {
        ObservationBuildResult result = ObservationBuilder.Build(
            ObservationTestData.CreateGameSnapshot(
            [
                ObservationTestData.Town(1, "Alpha", 2_000, 10, 10),
                ObservationTestData.Town(2, "Beta", 1_500, 20, 10),
                ObservationTestData.Town(3, "Gamma", 800, 15, 18),
            ]),
            ObservationTestData.CreateContext());

        ObservationOpportunity opportunity = Assert.Single(result.Snapshot.Sections.CandidateOpportunities.Opportunities.Take(1));

        Assert.Equal("population_then_distance", result.Snapshot.Sections.GoalContext.RankingRule);
        Assert.Equal(1, opportunity.SourceTownId);
        Assert.Equal(2, opportunity.DestinationTownId);
        Assert.Equal("passengers", opportunity.Cargo);
        Assert.True(opportunity.RankingScore > 0);
    }

    [Fact]
    public void EnforcesConfiguredBoundsBeforeCanonicalizingTheObservation()
    {
        ObservationBuildResult result = ObservationBuilder.Build(
            ObservationTestData.CreateGameSnapshot(),
            ObservationTestData.CreateContext(),
            new ObservationLimits(2, 1, 1, 1, 1, 1, 1, 1, 1));

        Assert.Equal(2, result.Snapshot.Sections.CandidateOpportunities.Towns.Count);
        Assert.Single(result.Snapshot.Sections.CandidateOpportunities.Opportunities);
        Assert.Single(result.Snapshot.Sections.NetworkSummary.Stations);
        Assert.Equal(50_000, result.Snapshot.Sections.ConstraintsAndBudgets.AvailableProjectBudget);
    }

    [Fact]
    public void RejectsAnObservationThatExceedsItsDeclaredReductionBudget()
    {
        ObservationBuildContext context = ObservationTestData.CreateContext() with
        {
            ReductionPolicy = new ObservationReductionPolicy(
                ObservationLimits.Default,
                "population_then_distance",
                MaximumCanonicalBytes: 1_024,
                MaximumEstimatedTokens: 256),
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => ObservationBuilder.Build(
            ObservationTestData.CreateGameSnapshot(),
            context));

        Assert.Contains("scenario-declared byte or estimated token budget", exception.Message, StringComparison.Ordinal);
    }
}
