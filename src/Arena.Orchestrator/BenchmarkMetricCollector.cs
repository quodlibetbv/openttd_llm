using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Orchestrator;

/// <summary>
/// Converts one authoritative ArenaGS snapshot into a stable score/replay
/// metric vector. This is intentionally provider-free and has no access to
/// rendering or recording state.
/// </summary>
public static class BenchmarkMetricCollector
{
    public static BenchmarkMetricSnapshot Capture(
        string runId,
        string sampleId,
        string kind,
        GameScriptSnapshot snapshot,
        int invalidDecisionCount,
        int constraintViolationCount)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ProtocolEnvelopeValidator.IsIdentifier(runId) ||
            !ProtocolEnvelopeValidator.IsIdentifier(sampleId) ||
            kind is not ("initial" or "periodic" or "final") ||
            invalidDecisionCount < 0 ||
            constraintViolationCount < 0)
        {
            throw new ArgumentException("The benchmark metric snapshot identity or counters are invalid.");
        }

        long infrastructureInvestment = SaturatingSum(snapshot.Projects.Select(project => Math.Max(0, project.Spent)));
        int activeVehicleCount = snapshot.Routes
            .Where(route => route.Operational)
            .SelectMany(route => route.VehicleIds)
            .Distinct()
            .Count();
        return new BenchmarkMetricSnapshot
        {
            SchemaVersion = ContractVersions.MetricV1,
            RunId = runId,
            SampleId = sampleId,
            Kind = kind,
            Source = "gamescript",
            GameDate = snapshot.GameDate,
            GameTick = Math.Max(0, snapshot.GameTick),
            Metrics = new BenchmarkMetrics
            {
                Cash = snapshot.Company.Cash,
                Loan = Math.Max(0, snapshot.Company.Loan),
                QuarterlyIncome = snapshot.Company.QuarterlyIncome,
                QuarterlyExpenses = snapshot.Company.QuarterlyExpenses,
                OperatingProfit = SaturatingSubtract(snapshot.Company.QuarterlyIncome, snapshot.Company.QuarterlyExpenses),
                CompanyValue = snapshot.Company.CompanyValue,
                QuarterlyCargoDelivered = Math.Max(0, snapshot.Company.QuarterlyCargoDelivered),
                ActiveVehicleCount = activeVehicleCount,
                OperationalRouteCount = snapshot.Routes.Count(route => route.Operational),
                CompletedProjectCount = snapshot.Projects.Count(project => string.Equals(project.State, "completed", StringComparison.Ordinal)),
                InfrastructureInvestment = infrastructureInvestment,
                InvalidDecisionCount = invalidDecisionCount,
                ConstraintViolationCount = constraintViolationCount,
            },
        };
    }

    private static long SaturatingSum(IEnumerable<long> values)
    {
        long result = 0;
        foreach (long value in values)
        {
            result = value > 0 && result > long.MaxValue - value
                ? long.MaxValue
                : result + value;
        }

        return result;
    }

    private static long SaturatingSubtract(long left, long right)
    {
        if (right > 0 && left < long.MinValue + right)
        {
            return long.MinValue;
        }

        if (right < 0 && left > long.MaxValue + right)
        {
            return long.MaxValue;
        }

        return left - right;
    }
}
