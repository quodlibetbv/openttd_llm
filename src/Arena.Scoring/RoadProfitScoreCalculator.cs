using System.Text.Json;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Scoring;

/// <summary>
/// Pure Phase 07 scorer. It consumes only an immutable scenario and recorded
/// authoritative metric snapshots; provider latency, cost, camera, and video
/// state are intentionally absent from the formula.
/// </summary>
public sealed class RoadProfitScoreCalculator : IScoreCalculator
{
    public const string FormulaId = "road-profit-v1";
    private const decimal Precision = 0.000001m;

    public ScoreResult Calculate(ScoreInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Scenario);
        ArgumentNullException.ThrowIfNull(input.FinalMetrics);
        ArgumentNullException.ThrowIfNull(input.PeriodicMetrics);
        BenchmarkScenario scenario = input.Scenario;
        BenchmarkMetricSnapshot final = input.FinalMetrics;
        if (!string.Equals(scenario.SchemaVersion, ContractVersions.ScenarioV1, StringComparison.Ordinal) ||
            !string.Equals(scenario.Scoring.ScoreSchemaVersion, ContractVersions.ScoreV1, StringComparison.Ordinal) ||
            !string.Equals(scenario.Scoring.FormulaId, FormulaId, StringComparison.Ordinal) ||
            !string.Equals(final.SchemaVersion, ContractVersions.MetricV1, StringComparison.Ordinal) ||
            !string.Equals(final.Kind, "final", StringComparison.Ordinal) ||
            !ProtocolEnvelopeValidator.IsIdentifier(final.RunId))
        {
            throw new ArgumentException("The scenario or final metric snapshot is outside the supported road-profit score contract.", nameof(input));
        }

        List<ScoreComponent> components = [];
        foreach (ScenarioScoreComponentDefinition definition in scenario.Scoring.Components)
        {
            MetricValue metric = ResolveMetric(definition.Metric, final.Metrics, scenario);
            decimal value = metric.IsMissing
                ? ResolveMissingValue(definition)
                : metric.Value;
            decimal normalized = Normalize(value, definition.Baseline, definition.Cap);
            decimal contribution = Round(normalized * definition.Weight);
            components.Add(new ScoreComponent
            {
                Key = definition.Key,
                Metric = definition.Metric,
                Units = definition.Units,
                Value = Round(value),
                Baseline = definition.Baseline,
                Cap = definition.Cap,
                Normalization = "clamp_linear",
                NormalizedValue = normalized,
                Weight = definition.Weight,
                Contribution = contribution,
                MissingDataBehavior = definition.MissingDataBehavior,
                PenaltyInteraction = definition.PenaltyInteraction,
            });
        }

        decimal totalPenalty = 0m;
        foreach (ScenarioPenalty penalty in scenario.Penalties)
        {
            long count = ResolvePenaltyCount(penalty.Trigger, final.Metrics);
            decimal amount = Round(count * penalty.Points);
            totalPenalty += amount;
            components.Add(new ScoreComponent
            {
                Key = "penalty_" + penalty.Key,
                Metric = penalty.Trigger + "_count",
                Units = "occurrences",
                Value = count,
                Baseline = 0m,
                Cap = 1m,
                Normalization = "count",
                NormalizedValue = count,
                Weight = penalty.Points,
                Contribution = -amount,
                MissingDataBehavior = "zero",
                PenaltyInteraction = "subtract_after_component_normalization",
            });
        }

        decimal total = Round(components.Sum(component => component.Contribution));
        string finalMetricsSha256 = CanonicalJson.ComputeSha256(JsonSerializer.SerializeToElement(final));
        return new ScoreResult
        {
            SchemaVersion = ContractVersions.ScoreV1,
            RunId = final.RunId,
            ScenarioId = scenario.ScenarioId,
            ScenarioVersion = scenario.Version,
            FormulaId = FormulaId,
            FinalMetricsSha256 = finalMetricsSha256,
            TotalScore = total,
            TotalPenalty = Round(totalPenalty),
            Components = components,
        };
    }

    private static MetricValue ResolveMetric(string metric, BenchmarkMetrics values, BenchmarkScenario scenario) => metric switch
    {
        "operating_profit" => MetricValue.From(values.OperatingProfit),
        "company_value" => MetricValue.From(values.CompanyValue),
        "cargo_delivered" => MetricValue.From(values.QuarterlyCargoDelivered),
        "profit_per_active_vehicle" => values.ActiveVehicleCount > 0
            ? MetricValue.From((decimal)values.OperatingProfit / values.ActiveVehicleCount)
            : MetricValue.Missing,
        "return_on_infrastructure" => values.InfrastructureInvestment > 0
            ? MetricValue.From((decimal)values.OperatingProfit / values.InfrastructureInvestment)
            : MetricValue.Missing,
        "solvency_completion" => MetricValue.From(IsSolventAndComplete(values, scenario) ? 1m : 0m),
        _ => throw new ArgumentException("The scenario references an unknown road-profit metric: " + metric, nameof(metric)),
    };

    private static bool IsSolventAndComplete(BenchmarkMetrics values, BenchmarkScenario scenario) =>
        values.Cash >= scenario.Constraints.MinimumCashReserve &&
        scenario.Objectives.All(objective => ResolveObjectiveMetric(objective.Metric, values) >= objective.Minimum);

    private static long ResolveObjectiveMetric(string metric, BenchmarkMetrics values) => metric switch
    {
        "operational_route_count" => values.OperationalRouteCount,
        "cargo_delivered" => values.QuarterlyCargoDelivered,
        "operating_profit" => values.OperatingProfit,
        _ => throw new ArgumentException("The scenario references an unknown objective metric: " + metric, nameof(metric)),
    };

    private static long ResolvePenaltyCount(string trigger, BenchmarkMetrics values) => trigger switch
    {
        "invalid_decision" => values.InvalidDecisionCount,
        "constraint_violation" => values.ConstraintViolationCount,
        _ => throw new ArgumentException("The scenario references an unknown penalty trigger: " + trigger, nameof(trigger)),
    };

    private static decimal ResolveMissingValue(ScenarioScoreComponentDefinition definition) =>
        string.Equals(definition.MissingDataBehavior, "zero", StringComparison.Ordinal)
            ? 0m
            : throw new InvalidOperationException("The score input is missing a metric whose scenario behavior is fail.");

    private static decimal Normalize(decimal value, decimal baseline, decimal cap)
    {
        if (cap <= baseline)
        {
            throw new ArgumentException("Every score component requires a cap greater than its baseline.");
        }

        decimal normalized = (value - baseline) / (cap - baseline);
        return Round(Math.Clamp(normalized, 0m, 1m));
    }

    private static decimal Round(decimal value) =>
        decimal.Round(value / Precision, 0, MidpointRounding.ToZero) * Precision;

    private readonly record struct MetricValue(decimal Value, bool IsMissing)
    {
        public static MetricValue Missing { get; } = new(0m, true);

        public static MetricValue From(decimal value) => new(value, false);
    }
}
