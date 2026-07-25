using System.Text.Json;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Orchestrator;
using OpenTtd.ModelArena.Storage;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class BenchmarkReplayTests
{
    [Fact]
    public async Task ReadsOnlyAcceptedActionsFromAClosedActionStream()
    {
        using TemporaryDirectory directory = new();
        string actionsPath = directory.WriteFile("actions.ndjson", string.Empty);
        RecordedAction accepted = CreateAction("action-accepted", "accepted");
        RecordedAction rejected = CreateAction("action-rejected", "rejected");
        await File.WriteAllTextAsync(
            actionsPath,
            CanonicalJson.SerializeToString(JsonSerializer.SerializeToElement(accepted)) + Environment.NewLine +
            CanonicalJson.SerializeToString(JsonSerializer.SerializeToElement(rejected)) + Environment.NewLine);

        AcceptedActionReplayReadResult result = await AcceptedActionReplayReader.ReadAsync(actionsPath, CancellationToken.None);

        Assert.True(result.Succeeded);
        RecordedAction selected = Assert.Single(result.AcceptedActions);
        Assert.Equal("action-accepted", selected.Request.ActionId);
    }

    [Fact]
    public async Task RejectsDuplicateMetricSampleIdentifiers()
    {
        using TemporaryDirectory directory = new();
        string metricsPath = directory.WriteFile("metrics.ndjson", string.Empty);
        BenchmarkMetricSnapshot metric = CreateFinalMetrics("metric-1");
        string line = CanonicalJson.SerializeToString(JsonSerializer.SerializeToElement(metric)) + Environment.NewLine;
        await File.WriteAllTextAsync(metricsPath, line + line);

        BenchmarkMetricReadResult result = await BenchmarkMetricSnapshotReader.ReadAsync(metricsPath, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ArenaErrorCodes.ArtifactVerificationFailed, result.ErrorCode);
    }

    [Fact]
    public void UsesOnlyDeclaredReplayTolerancesForFinalMetricEquivalence()
    {
        BenchmarkMetricSnapshot expected = CreateFinalMetrics("metric-expected");
        BenchmarkMetricSnapshot withinTolerance = expected with
        {
            RunId = "run-0002",
            SampleId = "metric-actual",
            Metrics = expected.Metrics with { Cash = expected.Metrics.Cash + 100 },
        };
        ReplayMetricTolerances tolerances = new()
        {
            Cash = 100,
            OperatingProfit = 0,
            CompanyValue = 0,
            CargoDelivered = 0,
            ActiveVehicleCount = 0,
            OperationalRouteCount = 0,
            InfrastructureInvestment = 0,
        };

        ReplayMetricComparisonResult pass = ReplayMetricComparator.Compare(expected, withinTolerance, tolerances);
        ReplayMetricComparisonResult fail = ReplayMetricComparator.Compare(
            expected,
            withinTolerance with { Metrics = withinTolerance.Metrics with { Cash = withinTolerance.Metrics.Cash + 1 } },
            tolerances);

        Assert.True(pass.Succeeded);
        Assert.False(fail.Succeeded);
        ReplayMetricDifference difference = Assert.Single(fail.Differences);
        Assert.Equal("cash", difference.Metric);
    }

    [Fact]
    public void BuildsByteEquivalentCommonProviderRequestsRegardlessOfAdapterModel()
    {
        ObservationBuildResult observation = ObservationTestData.BuildSnapshot();
        ProviderDecisionExecutionOptions replayOptions = new(
            ObservationTestData.CreateContext(),
            "decision-0002",
            "road-profit-replay-v1",
            TimeSpan.FromSeconds(20),
            MaximumActions: 1);
        ProviderDecisionExecutionOptions deepSeekOptions = replayOptions with { ProviderModel = "deepseek-v4-flash" };

        ModelRequest replay = ProviderRequestFactory.Create(observation, replayOptions);
        ModelRequest deepSeek = ProviderRequestFactory.Create(observation, deepSeekOptions);

        Assert.Equal(
            CanonicalJson.SerializeToString(JsonSerializer.SerializeToElement(replay)),
            CanonicalJson.SerializeToString(JsonSerializer.SerializeToElement(deepSeek)));
    }

    private static RecordedAction CreateAction(string actionId, string status)
    {
        ActionRequest request = new()
        {
            ActionId = actionId,
            RunId = "run-0001",
            DecisionId = "decision-0001",
            CorrelationId = actionId + "-correlation",
            IdempotencyKey = actionId + "-key",
            Tool = RoadToolCatalog.BuildTransportRoute,
            Arguments = JsonSerializer.SerializeToElement(new
            {
                mode = "road",
                source_town_id = 1,
                destination_town_id = 2,
                cargo = "passengers",
                initial_vehicle_count = 1,
                maximum_budget = 40_000,
            }),
        };
        return new RecordedAction
        {
            SchemaVersion = ContractVersions.ObservationV1,
            RunId = "run-0001",
            DecisionId = "decision-0001",
            Request = request,
            Result = new ActionResult
            {
                ActionId = actionId,
                RunId = "run-0001",
                CorrelationId = request.CorrelationId,
                Status = status,
                ErrorCode = status == "accepted" ? null : ArenaErrorCodes.ActionConstraintViolation,
                Message = "Recorded action result.",
            },
        };
    }

    private static BenchmarkMetricSnapshot CreateFinalMetrics(string sampleId) => new()
    {
        SchemaVersion = ContractVersions.MetricV1,
        RunId = "run-0001",
        SampleId = sampleId,
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
            ActiveVehicleCount = 1,
            OperationalRouteCount = 1,
            CompletedProjectCount = 1,
            InfrastructureInvestment = 20_000,
            InvalidDecisionCount = 0,
            ConstraintViolationCount = 0,
        },
    };
}
