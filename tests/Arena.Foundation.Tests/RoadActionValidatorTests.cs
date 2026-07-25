using OpenTtd.ModelArena.Contracts;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class RoadActionValidatorTests
{
    [Fact]
    public void AcceptsABoundedRoadRouteForTownsFromTheLatestSnapshot()
    {
        ObservationSnapshot snapshot = ObservationTestData.BuildSnapshot().Snapshot;
        ModelAction action = ObservationTestData.Action(
            RoadToolCatalog.BuildTransportRoute,
            """
            {
              "mode": "road",
              "source_town_id": 1,
              "destination_town_id": 2,
              "cargo": "passengers",
              "initial_vehicle_count": 2,
              "maximum_budget": 40000
            }
            """);

        RoadActionValidationResult result = RoadActionValidator.Validate(
            action,
            snapshot,
            RoadToolCatalog.All.ToHashSet(StringComparer.Ordinal));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void RejectsAStaleTownReferenceAndBudgetAboveTheLatestSnapshot()
    {
        ObservationSnapshot snapshot = ObservationTestData.BuildSnapshot().Snapshot;
        ModelAction staleTown = ObservationTestData.Action(
            RoadToolCatalog.BuildTransportRoute,
            """
            {
              "mode": "road",
              "source_town_id": 99,
              "destination_town_id": 2,
              "cargo": "passengers",
              "initial_vehicle_count": 1,
              "maximum_budget": 40000
            }
            """);
        ModelAction excessiveBudget = ObservationTestData.Action(
            RoadToolCatalog.BuildTransportRoute,
            """
            {
              "mode": "road",
              "source_town_id": 1,
              "destination_town_id": 2,
              "cargo": "passengers",
              "initial_vehicle_count": 1,
              "maximum_budget": 50001
            }
            """);

        RoadActionValidationResult staleResult = RoadActionValidator.Validate(
            staleTown,
            snapshot,
            RoadToolCatalog.All.ToHashSet(StringComparer.Ordinal));
        RoadActionValidationResult budgetResult = RoadActionValidator.Validate(
            excessiveBudget,
            snapshot,
            RoadToolCatalog.All.ToHashSet(StringComparer.Ordinal));

        Assert.False(staleResult.IsValid);
        Assert.False(budgetResult.IsValid);
        Assert.Equal(ArenaErrorCodes.ActionConstraintViolation, staleResult.ErrorCode);
        Assert.Equal(ArenaErrorCodes.ActionConstraintViolation, budgetResult.ErrorCode);
    }

    [Fact]
    public void AcceptsTheExactAvailableProjectBudgetAndRejectsOneUnitAboveIt()
    {
        ObservationSnapshot snapshot = ObservationTestData.BuildSnapshot().Snapshot;
        long availableBudget = snapshot.Sections.ConstraintsAndBudgets.AvailableProjectBudget;
        ModelAction exactBudget = ObservationTestData.Action(
            RoadToolCatalog.BuildTransportRoute,
            $$"""
            {
              "mode": "road",
              "source_town_id": 1,
              "destination_town_id": 2,
              "cargo": "passengers",
              "initial_vehicle_count": 1,
              "maximum_budget": {{availableBudget}}
            }
            """);
        ModelAction oneUnitOver = ObservationTestData.Action(
            RoadToolCatalog.BuildTransportRoute,
            $$"""
            {
              "mode": "road",
              "source_town_id": 1,
              "destination_town_id": 2,
              "cargo": "passengers",
              "initial_vehicle_count": 1,
              "maximum_budget": {{availableBudget + 1}}
            }
            """);

        RoadActionValidationResult exactResult = RoadActionValidator.Validate(
            exactBudget,
            snapshot,
            RoadToolCatalog.All.ToHashSet(StringComparer.Ordinal));
        RoadActionValidationResult overResult = RoadActionValidator.Validate(
            oneUnitOver,
            snapshot,
            RoadToolCatalog.All.ToHashSet(StringComparer.Ordinal));

        Assert.True(exactResult.IsValid);
        Assert.False(overResult.IsValid);
        Assert.Equal(ArenaErrorCodes.ActionConstraintViolation, overResult.ErrorCode);
    }

    [Fact]
    public void RequiresAnOperationalKnownRouteAndBudgetForFleetPurchases()
    {
        ObservationSnapshot snapshot = ObservationTestData.BuildSnapshot().Snapshot;
        ModelAction expand = ObservationTestData.Action(
            RoadToolCatalog.ExpandRoute,
            """
            {
              "route_id": "route-4-5",
              "vehicle_count": 2,
              "maximum_budget": 40000
            }
            """);
        ModelAction missingBudget = ObservationTestData.Action(
            RoadToolCatalog.ReplaceVehicles,
            """
            {
              "route_id": "route-4-5",
              "vehicle_count": 1
            }
            """);
        ModelAction noReduction = ObservationTestData.Action(
            RoadToolCatalog.ReduceRoute,
            """
            {
              "route_id": "route-4-5",
              "vehicle_count": 1
            }
            """);

        RoadActionValidationResult expandResult = RoadActionValidator.Validate(
            expand,
            snapshot,
            RoadToolCatalog.All.ToHashSet(StringComparer.Ordinal));
        RoadActionValidationResult missingBudgetResult = RoadActionValidator.Validate(
            missingBudget,
            snapshot,
            RoadToolCatalog.All.ToHashSet(StringComparer.Ordinal));
        RoadActionValidationResult noReductionResult = RoadActionValidator.Validate(
            noReduction,
            snapshot,
            RoadToolCatalog.All.ToHashSet(StringComparer.Ordinal));

        Assert.True(expandResult.IsValid);
        Assert.False(missingBudgetResult.IsValid);
        Assert.False(noReductionResult.IsValid);
    }
}
