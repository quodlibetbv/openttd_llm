using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Orchestrator;

/// <summary>
/// Classifies the bounded one-action road-profit decision independently from
/// provider transport errors. A rejected scenario action is a declared
/// constraint violation; malformed, empty, or objective-incompatible output
/// is an invalid decision. Both categories remain visible in final metrics so
/// a sealed unsuccessful run can be scored deterministically.
/// </summary>
public sealed record BenchmarkDecisionOutcome(
    bool AcceptedRoute,
    int InvalidDecisionCount,
    int ConstraintViolationCount,
    ArenaError? TerminalError,
    string TerminalDetail);

public static class BenchmarkDecisionOutcomeClassifier
{
    public static BenchmarkDecisionOutcome Classify(ProviderDecisionExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        IReadOnlyList<ActionResult> actionResults = result.ActionResults;
        ModelDecision? decision = result.Decision;
        ActionResult? action = actionResults.Count == 1 ? actionResults[0] : null;
        bool hasOneDecisionAction = decision is { Actions.Count: 1 };
        bool selectedRequiredRoute = hasOneDecisionAction &&
            string.Equals(decision!.Actions[0].Tool, RoadToolCatalog.BuildTransportRoute, StringComparison.Ordinal);
        bool acceptedRoute = result.Succeeded && selectedRequiredRoute &&
            action is not null &&
            string.Equals(action.Status, "accepted", StringComparison.Ordinal);
        if (acceptedRoute)
        {
            return new BenchmarkDecisionOutcome(true, 0, 0, null, "The provider selected one accepted benchmark route action.");
        }

        int constraintViolations = actionResults.Count(entry =>
            string.Equals(entry.ErrorCode, ArenaErrorCodes.ActionConstraintViolation, StringComparison.Ordinal));
        bool invalidDecision = IsInvalidDecision(result, hasOneDecisionAction, selectedRequiredRoute);
        ArenaError terminalError = result.Error ?? new ArenaError(
            action?.ErrorCode ?? (invalidDecision ? ArenaErrorCodes.ProviderInvalidOutput : ArenaErrorCodes.ActionConstraintViolation),
            action?.Message ?? "The benchmark provider did not produce one accepted build_transport_route action.",
            "The bounded road-profit decision outcome did not satisfy the required accepted route action.",
            false);
        return new BenchmarkDecisionOutcome(
            false,
            invalidDecision ? 1 : 0,
            constraintViolations,
            terminalError,
            "The benchmark provider did not produce exactly one accepted build_transport_route action while simulation was paused.");
    }

    private static bool IsInvalidDecision(
        ProviderDecisionExecutionResult result,
        bool hasOneDecisionAction,
        bool selectedRequiredRoute)
    {
        if (!result.Succeeded)
        {
            return string.Equals(result.Error?.Code, ArenaErrorCodes.ProviderInvalidOutput, StringComparison.Ordinal) ||
                string.Equals(result.Error?.Code, ArenaErrorCodes.ProviderInvalidJson, StringComparison.Ordinal) ||
                string.Equals(result.Error?.Code, ArenaErrorCodes.ProviderSchemaMismatch, StringComparison.Ordinal);
        }

        return result.Decision is null || !hasOneDecisionAction || !selectedRequiredRoute;
    }
}
