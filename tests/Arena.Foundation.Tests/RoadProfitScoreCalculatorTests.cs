using System.Text.Json;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Orchestrator;
using OpenTtd.ModelArena.Scoring;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class RoadProfitScoreCalculatorTests
{
    [Fact]
    public async Task ProducesTheSameDetailedScoreForTheSameScenarioAndMetrics()
    {
        BenchmarkScenario scenario = await LoadSmokeScenarioAsync();
        BenchmarkMetricSnapshot finalMetrics = CreateFinalMetrics(invalidDecisions: 2, constraintViolations: 1);
        RoadProfitScoreCalculator calculator = new();

        ScoreResult first = calculator.Calculate(new ScoreInput(scenario, finalMetrics, []));
        ScoreResult second = calculator.Calculate(new ScoreInput(scenario, finalMetrics, []));

        Assert.Equal(CanonicalJson.SerializeToString(JsonSerializer.SerializeToElement(first)), CanonicalJson.SerializeToString(JsonSerializer.SerializeToElement(second)));
        Assert.Equal(40m, first.TotalPenalty);
        Assert.Equal(
            [
                "operating_profit",
                "company_value",
                "cargo_delivered",
                "profit_per_active_vehicle",
                "return_on_infrastructure",
                "solvency_completion",
                "penalty_invalid_decision",
                "penalty_constraint_violation",
            ],
            first.Components.Select(component => component.Key));
        Assert.Equal(CanonicalJson.ComputeSha256(JsonSerializer.SerializeToElement(finalMetrics)), first.FinalMetricsSha256);
    }

    [Fact]
    public async Task AppliesDeclaredMissingDataBehaviorWhenNoVehicleOrInfrastructureExists()
    {
        BenchmarkScenario scenario = await LoadSmokeScenarioAsync();
        BenchmarkMetricSnapshot finalMetrics = CreateFinalMetrics(activeVehicles: 0, infrastructureInvestment: 0);

        ScoreResult score = new RoadProfitScoreCalculator().Calculate(new ScoreInput(scenario, finalMetrics, []));

        Assert.Equal(0m, score.Components.Single(component => component.Key == "profit_per_active_vehicle").Value);
        Assert.Equal(0m, score.Components.Single(component => component.Key == "return_on_infrastructure").Value);
    }

    private static async Task<BenchmarkScenario> LoadSmokeScenarioAsync()
    {
        string root = FindRepositoryRoot();
        ScenarioLoadResult loaded = await ScenarioLoader.LoadAsync(
            root,
            Path.Combine(root, "scenarios", "road-profit-smoke-v1.yaml"),
            CancellationToken.None);
        return Assert.IsType<ScenarioDocument>(loaded.Document).Scenario;
    }

    private static BenchmarkMetricSnapshot CreateFinalMetrics(
        int invalidDecisions = 0,
        int constraintViolations = 0,
        int activeVehicles = 1,
        long infrastructureInvestment = 20_000) =>
        new()
        {
            SchemaVersion = ContractVersions.MetricV1,
            RunId = "run-0001",
            SampleId = "metric-final-1",
            Kind = "final",
            Source = "gamescript",
            GameDate = "1950-01-01",
            GameTick = 42,
            Metrics = new BenchmarkMetrics
            {
                Cash = 90_000,
                Loan = 0,
                QuarterlyIncome = 10_000,
                QuarterlyExpenses = 4_000,
                OperatingProfit = 6_000,
                CompanyValue = 120_000,
                QuarterlyCargoDelivered = 250,
                ActiveVehicleCount = activeVehicles,
                OperationalRouteCount = 1,
                CompletedProjectCount = 1,
                InfrastructureInvestment = infrastructureInvestment,
                InvalidDecisionCount = invalidDecisions,
                ConstraintViolationCount = constraintViolations,
            },
        };

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenTTD.ModelArena.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The test repository root is unavailable.");
    }
}
