using OpenTtd.ModelArena.Contracts;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class ScenarioActionConstraintValidatorTests
{
    [Fact]
    public void AllowsTheExactScenarioBudgetButBlocksAnAdditionalActiveRouteProject()
    {
        ObservationSnapshot baseSnapshot = ObservationTestData.BuildSnapshot().Snapshot;
        ObservationSnapshot activeProjectSnapshot = baseSnapshot with
        {
            Sections = baseSnapshot.Sections with
            {
                ActiveProjects =
                [
                    new ObservationProject
                    {
                        ProjectId = "project-active-1",
                        ActionId = "action-active-1",
                        State = "building_infrastructure",
                        Spent = 0,
                        MaximumBudget = 40_000,
                    },
                ],
            },
        };
        ScenarioActionConstraintContext constraints = CreateConstraints();
        ModelAction action = ObservationTestData.Action(
            RoadToolCatalog.BuildTransportRoute,
            """
            {
              "mode": "road",
              "source_town_id": 1,
              "destination_town_id": 2,
              "cargo": "passengers",
              "initial_vehicle_count": 1,
              "maximum_budget": 40000
            }
            """);

        RoadActionValidationResult allowed = ScenarioActionConstraintValidator.Validate(action, baseSnapshot, constraints);
        RoadActionValidationResult blocked = ScenarioActionConstraintValidator.Validate(action, activeProjectSnapshot, constraints);

        Assert.True(allowed.IsValid);
        Assert.False(blocked.IsValid);
        Assert.Equal(ArenaErrorCodes.ActionConstraintViolation, blocked.ErrorCode);
    }

    [Fact]
    public void BlocksAProjectBudgetOverTheScenarioCeilingEvenWhenTheObservationCanAffordIt()
    {
        ObservationSnapshot snapshot = ObservationTestData.BuildSnapshot().Snapshot;
        ModelAction overBudget = ObservationTestData.Action(
            RoadToolCatalog.BuildTransportRoute,
            """
            {
              "mode": "road",
              "source_town_id": 1,
              "destination_town_id": 2,
              "cargo": "passengers",
              "initial_vehicle_count": 1,
              "maximum_budget": 45000
            }
            """);

        RoadActionValidationResult result = ScenarioActionConstraintValidator.Validate(overBudget, snapshot, CreateConstraints());

        Assert.False(result.IsValid);
        Assert.Equal(ArenaErrorCodes.ActionConstraintViolation, result.ErrorCode);
    }

    [Fact]
    public void BlocksDisallowedToolsAndLoanRepaymentPastTheScenarioCashReserve()
    {
        ObservationSnapshot snapshot = ObservationTestData.BuildSnapshot().Snapshot;
        ScenarioActionConstraintContext routeOnly = CreateConstraints() with
        {
            AllowedTools = [RoadToolCatalog.BuildTransportRoute],
        };
        ModelAction wait = ObservationTestData.Action(
            RoadToolCatalog.Wait,
            """
            { "game_days": 1 }
            """);
        ModelAction exactReserveRepayment = ObservationTestData.Action(
            RoadToolCatalog.RepayLoan,
            """
            { "amount": 90000 }
            """);
        ModelAction reserveBreachingRepayment = ObservationTestData.Action(
            RoadToolCatalog.RepayLoan,
            """
            { "amount": 90001 }
            """);

        RoadActionValidationResult disallowedTool = ScenarioActionConstraintValidator.Validate(wait, snapshot, routeOnly);
        RoadActionValidationResult exactReserve = ScenarioActionConstraintValidator.Validate(
            exactReserveRepayment,
            snapshot,
            CreateConstraints());
        RoadActionValidationResult reserveBreach = ScenarioActionConstraintValidator.Validate(
            reserveBreachingRepayment,
            snapshot,
            CreateConstraints());

        Assert.False(disallowedTool.IsValid);
        Assert.Equal(ArenaErrorCodes.ActionConstraintViolation, disallowedTool.ErrorCode);
        Assert.True(exactReserve.IsValid);
        Assert.False(reserveBreach.IsValid);
        Assert.Equal(ArenaErrorCodes.ActionConstraintViolation, reserveBreach.ErrorCode);
    }

    private static ScenarioActionConstraintContext CreateConstraints() => new()
    {
        ScenarioId = "road-profit-smoke",
        ScenarioVersion = "1.0.0",
        ScenarioSha256 = new string('a', 64),
        MinimumCashReserve = 10_000,
        PerProjectBudget = 40_000,
        MaximumActiveProjects = 1,
        AllowedModes = ["road"],
        AllowedCargo = ["passengers"],
        AllowedTools = RoadToolCatalog.All,
    };
}
