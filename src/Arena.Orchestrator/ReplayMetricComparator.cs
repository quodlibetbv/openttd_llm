using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Orchestrator;

public sealed record ReplayMetricDifference(
    string Metric,
    long Expected,
    long Actual,
    long Tolerance);

public sealed record ReplayMetricComparisonResult(
    bool Succeeded,
    string? ErrorCode,
    string Detail,
    IReadOnlyList<ReplayMetricDifference> Differences)
{
    public static ReplayMetricComparisonResult Failure(string detail) => new(
        false,
        ArenaErrorCodes.ReplayMetricsMismatch,
        detail,
        []);
}

/// <summary>
/// Compares only the scenario-declared normalized final metric vector.
/// Provider latency, output text, recordings, and run identifiers never enter
/// the replay equivalence decision.
/// </summary>
public static class ReplayMetricComparator
{
    public static ReplayMetricComparisonResult Compare(
        BenchmarkMetricSnapshot expected,
        BenchmarkMetricSnapshot actual,
        ReplayMetricTolerances tolerances)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(tolerances);
        if (!string.Equals(expected.Kind, "final", StringComparison.Ordinal) ||
            !string.Equals(actual.Kind, "final", StringComparison.Ordinal))
        {
            return ReplayMetricComparisonResult.Failure("Accepted-action replay must compare two final authoritative metric snapshots.");
        }

        (string Metric, long Expected, long Actual, long Tolerance)[] values =
        [
            ("cash", expected.Metrics.Cash, actual.Metrics.Cash, tolerances.Cash),
            ("operating_profit", expected.Metrics.OperatingProfit, actual.Metrics.OperatingProfit, tolerances.OperatingProfit),
            ("company_value", expected.Metrics.CompanyValue, actual.Metrics.CompanyValue, tolerances.CompanyValue),
            ("cargo_delivered", expected.Metrics.QuarterlyCargoDelivered, actual.Metrics.QuarterlyCargoDelivered, tolerances.CargoDelivered),
            ("active_vehicle_count", expected.Metrics.ActiveVehicleCount, actual.Metrics.ActiveVehicleCount, tolerances.ActiveVehicleCount),
            ("operational_route_count", expected.Metrics.OperationalRouteCount, actual.Metrics.OperationalRouteCount, tolerances.OperationalRouteCount),
            ("infrastructure_investment", expected.Metrics.InfrastructureInvestment, actual.Metrics.InfrastructureInvestment, tolerances.InfrastructureInvestment),
        ];
        List<ReplayMetricDifference> differences = [];
        foreach ((string metric, long expectedValue, long actualValue, long tolerance) in values)
        {
            if (tolerance < 0 || AbsoluteDifference(expectedValue, actualValue) > tolerance)
            {
                differences.Add(new ReplayMetricDifference(metric, expectedValue, actualValue, Math.Max(0, tolerance)));
            }
        }

        return differences.Count == 0
            ? new ReplayMetricComparisonResult(
                true,
                null,
                "Accepted-action replay reproduced every scenario-declared final metric within its documented engine tolerance.",
                [])
            : new ReplayMetricComparisonResult(
                false,
                ArenaErrorCodes.ReplayMetricsMismatch,
                "Accepted-action replay exceeded one or more documented engine metric tolerances.",
                differences);
    }

    private static long AbsoluteDifference(long left, long right)
    {
        decimal difference = decimal.Abs((decimal)left - right);
        return difference > long.MaxValue ? long.MaxValue : (long)difference;
    }
}
