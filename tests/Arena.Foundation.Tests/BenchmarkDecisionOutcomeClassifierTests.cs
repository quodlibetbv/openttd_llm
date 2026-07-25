using System.Text.Json;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Orchestrator;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class BenchmarkDecisionOutcomeClassifierTests
{
    [Fact]
    public void AcceptsExactlyOneAcceptedRouteWithoutReliabilityPenalties()
    {
        BenchmarkDecisionOutcome outcome = BenchmarkDecisionOutcomeClassifier.Classify(CreateResult(
            succeeded: true,
            tool: RoadToolCatalog.BuildTransportRoute,
            status: "accepted"));

        Assert.True(outcome.AcceptedRoute);
        Assert.Equal(0, outcome.InvalidDecisionCount);
        Assert.Equal(0, outcome.ConstraintViolationCount);
        Assert.Null(outcome.TerminalError);
    }

    [Fact]
    public void RecordsScenarioRejectionsAsConstraintViolationsInsteadOfDroppingTheRun()
    {
        BenchmarkDecisionOutcome outcome = BenchmarkDecisionOutcomeClassifier.Classify(CreateResult(
            succeeded: true,
            tool: RoadToolCatalog.BuildTransportRoute,
            status: "rejected",
            errorCode: ArenaErrorCodes.ActionConstraintViolation));

        Assert.False(outcome.AcceptedRoute);
        Assert.Equal(0, outcome.InvalidDecisionCount);
        Assert.Equal(1, outcome.ConstraintViolationCount);
        Assert.Equal(ArenaErrorCodes.ActionConstraintViolation, outcome.TerminalError?.Code);
    }

    [Fact]
    public void RecordsMalformedOrObjectiveIncompatibleOutputAsAnInvalidDecision()
    {
        ProviderDecisionExecutionResult malformed = ProviderDecisionExecutionResult.Failure(new ArenaError(
            ArenaErrorCodes.ProviderInvalidJson,
            "The provider response was not valid JSON.",
            "Synthetic malformed provider output.",
            true));
        BenchmarkDecisionOutcome malformedOutcome = BenchmarkDecisionOutcomeClassifier.Classify(malformed);
        BenchmarkDecisionOutcome wrongToolOutcome = BenchmarkDecisionOutcomeClassifier.Classify(CreateResult(
            succeeded: true,
            tool: RoadToolCatalog.Wait,
            status: "accepted"));

        Assert.Equal(1, malformedOutcome.InvalidDecisionCount);
        Assert.Equal(0, malformedOutcome.ConstraintViolationCount);
        Assert.Equal(1, wrongToolOutcome.InvalidDecisionCount);
        Assert.Equal(0, wrongToolOutcome.ConstraintViolationCount);
    }

    [Fact]
    public void LeavesProviderTransportFailuresOutOfTheScenarioReliabilityPenalty()
    {
        ProviderDecisionExecutionResult unavailable = ProviderDecisionExecutionResult.Failure(new ArenaError(
            ArenaErrorCodes.ProviderTimeout,
            "The provider did not respond within the configured request deadline.",
            "Synthetic provider transport failure.",
            true));

        BenchmarkDecisionOutcome outcome = BenchmarkDecisionOutcomeClassifier.Classify(unavailable);

        Assert.False(outcome.AcceptedRoute);
        Assert.Equal(0, outcome.InvalidDecisionCount);
        Assert.Equal(0, outcome.ConstraintViolationCount);
        Assert.Equal(ArenaErrorCodes.ProviderTimeout, outcome.TerminalError?.Code);
    }

    private static ProviderDecisionExecutionResult CreateResult(
        bool succeeded,
        string tool,
        string status,
        string? errorCode = null)
    {
        ModelDecision decision = new()
        {
            DecisionId = "decision-road-profit-1",
            PublicSummary = "Synthetic benchmark decision.",
            Observations = ["Synthetic observation."],
            Actions =
            [
                new ModelAction
                {
                    Tool = tool,
                    Arguments = JsonSerializer.SerializeToElement(new { game_days = 1 }),
                },
            ],
            NextReviewGameDays = 1,
        };
        ActionResult action = new()
        {
            ActionId = "action-1",
            RunId = "run-0001",
            CorrelationId = "correlation-1",
            Status = status,
            ErrorCode = errorCode,
            Message = "Synthetic action outcome.",
        };
        return new ProviderDecisionExecutionResult(succeeded, null, decision, [action], null);
    }
}
