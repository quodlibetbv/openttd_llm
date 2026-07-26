using System.Text.Json;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Orchestrator;

namespace OpenTtd.ModelArena.Foundation.Tests;

internal static class ObservationTestData
{
    public static GameScriptSnapshot CreateGameSnapshot(
        IReadOnlyList<GameTownState>? towns = null,
        IReadOnlyList<NormalizedGameEvent>? events = null)
    {
        return new GameScriptSnapshot
        {
            SchemaVersion = ContractVersions.ObservationV1,
            GameDate = "1950-01-01",
            Paused = true,
            GameTick = 42,
            Company = new GameCompanyState
            {
                CompanyId = 1,
                Name = "Arena Transit",
                Cash = 100_000,
                Loan = 0,
                QuarterlyIncome = 10_000,
                QuarterlyExpenses = 4_000,
                QuarterlyCargoDelivered = 250,
                CompanyValue = 110_000,
                PerformanceRating = 50,
            },
            Towns = towns ??
            [
                Town(1, "Alpha", 2_000, 10, 10),
                Town(2, "Beta", 1_500, 20, 10),
                Town(3, "Gamma", 800, 15, 18),
            ],
            Industries =
            [
                new GameIndustryState
                {
                    IndustryId = 7,
                    Name = "North Mill",
                    Location = Coordinate(14, 5),
                },
            ],
            Stations =
            [
                new GameStationState
                {
                    StationId = 4,
                    Name = "Alpha Central",
                    Location = Coordinate(10, 10),
                    VehicleCount = 1,
                },
            ],
            Vehicles =
            [
                new GameVehicleState
                {
                    VehicleId = 9,
                    Name = "Bus 1",
                    VehicleType = "road",
                    State = "running",
                    ProfitLastYear = 2_000,
                    Location = Coordinate(11, 10),
                },
            ],
            Routes =
            [
                new GameRouteState
                {
                    RouteId = "route-4-5",
                    ActionId = "action-0001",
                    SourceStationId = 4,
                    DestinationStationId = 5,
                    Cargo = "passengers",
                    VehicleIds = [9],
                    Operational = true,
                },
            ],
            Projects =
            [
                new GameProjectState
                {
                    ProjectId = "project-0001",
                    ActionId = "action-0001",
                    State = "completed",
                    Spent = 20_000,
                    MaximumBudget = 25_000,
                    FailureCode = "ARENA-ACTION-PATH-NOT-FOUND",
                },
            ],
            Events = events ??
            [
                new NormalizedGameEvent
                {
                    EventId = "event-0002",
                    EventCode = "ARENA-ROUTE-OPERATING",
                    GameDate = "1950-01-01",
                    EntityIds = ["route-4-5", "vehicle-9"],
                    PublicSummary = "Route is operating.",
                    CorrelationId = "correlation-0001",
                },
            ],
        };
    }

    public static ObservationBuildContext CreateContext() => new(
        RunId: "run-0001",
        ScenarioId: "road-smoke",
        ScenarioVersion: "1.0.0",
        GoalId: "road-profit",
        GoalVersion: "1.0.0",
        GoalTitle: "Road profit",
        GoalObjective: "Build a safe passenger route.",
        AllowedTools: RoadToolCatalog.All,
        MinimumCashReserve: 10_000,
        PerProjectBudget: 50_000,
        RemainingModelCalls: 3,
        RemainingOutputTokens: 512,
        RemainingRetries: 1,
        PriorDecisionResults:
        [
            new ObservationDecisionResult
            {
                DecisionId = "decision-0001",
                ActionId = "action-0001",
                Status = "completed",
                Message = "Route setup completed.",
            },
        ]);

    public static ObservationBuildResult BuildSnapshot() =>
        ObservationBuilder.Build(CreateGameSnapshot(), CreateContext());

    public static ModelAction Action(string tool, string argumentsJson)
    {
        using JsonDocument document = JsonDocument.Parse(argumentsJson);
        return new ModelAction
        {
            Tool = tool,
            Arguments = document.RootElement.Clone(),
        };
    }

    public static GameTownState Town(int townId, string name, int population, int x, int y) => new()
    {
        TownId = townId,
        Name = name,
        Population = population,
        Location = Coordinate(x, y),
    };

    public static TileCoordinate Coordinate(int x, int y) => new() { X = x, Y = y };
}
