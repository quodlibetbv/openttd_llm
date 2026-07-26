using System.Text.Json;

namespace OpenTtd.ModelArena.Contracts;

/// <summary>
/// Applies scenario-owned constraints before an action crosses AdminPort. The
/// equivalent context travels with the trusted action envelope and is checked
/// again by ArenaGS; this validator is intentionally not the sole authority.
/// </summary>
public static class ScenarioActionConstraintValidator
{
    public static RoadActionValidationResult Validate(
        ModelAction action,
        ObservationSnapshot snapshot,
        ScenarioActionConstraintContext? constraints)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (constraints is null)
        {
            return RoadActionValidationResult.Valid;
        }

        if (!IsValidContext(constraints) || !constraints.AllowedTools.Contains(action.Tool, StringComparer.Ordinal))
        {
            return RoadActionValidationResult.Invalid("The selected tool is not allowed by the immutable scenario constraints.");
        }

        int activeProjects = snapshot.Sections.ActiveProjects.Count(project =>
            !string.Equals(project.State, "completed", StringComparison.Ordinal) &&
            !string.Equals(project.State, "failed", StringComparison.Ordinal));
        if (string.Equals(action.Tool, RoadToolCatalog.BuildTransportRoute, StringComparison.Ordinal) &&
            activeProjects >= constraints.MaximumActiveProjects)
        {
            return RoadActionValidationResult.Invalid("The scenario does not allow another active route project.");
        }

        long availableBudget = Math.Max(0, snapshot.Sections.FinancialSummary.Cash - constraints.MinimumCashReserve);
        return action.Tool switch
        {
            RoadToolCatalog.BuildTransportRoute => ValidateRouteBuild(action.Arguments, constraints, availableBudget),
            RoadToolCatalog.ExpandRoute or RoadToolCatalog.ReplaceVehicles => ValidatePurchaseBudget(action.Arguments, constraints, availableBudget),
            RoadToolCatalog.RepayLoan => ValidateLoanRepayment(action.Arguments, availableBudget),
            _ => RoadActionValidationResult.Valid,
        };
    }

    private static RoadActionValidationResult ValidateRouteBuild(
        JsonElement arguments,
        ScenarioActionConstraintContext constraints,
        long availableBudget)
    {
        if (!TryGetString(arguments, "mode", out string? mode) ||
            !TryGetString(arguments, "cargo", out string? cargo) ||
            !TryGetLong(arguments, "maximum_budget", out long maximumBudget) ||
            !constraints.AllowedModes.Contains(mode, StringComparer.Ordinal) ||
            !constraints.AllowedCargo.Contains(cargo, StringComparer.Ordinal) ||
            maximumBudget > constraints.PerProjectBudget ||
            maximumBudget > availableBudget)
        {
            return RoadActionValidationResult.Invalid("The route request exceeds the scenario's mode, cargo, reserve, or project-budget constraint.");
        }

        return RoadActionValidationResult.Valid;
    }

    private static RoadActionValidationResult ValidatePurchaseBudget(
        JsonElement arguments,
        ScenarioActionConstraintContext constraints,
        long availableBudget)
    {
        return TryGetLong(arguments, "maximum_budget", out long maximumBudget) &&
            maximumBudget <= constraints.PerProjectBudget &&
            maximumBudget <= availableBudget
            ? RoadActionValidationResult.Valid
            : RoadActionValidationResult.Invalid("The fleet purchase exceeds the scenario's cash reserve or project-budget constraint.");
    }

    private static RoadActionValidationResult ValidateLoanRepayment(JsonElement arguments, long availableBudget) =>
        TryGetLong(arguments, "amount", out long amount) && amount <= availableBudget
            ? RoadActionValidationResult.Valid
            : RoadActionValidationResult.Invalid("The loan repayment would breach the scenario cash reserve.");

    private static bool IsValidContext(ScenarioActionConstraintContext constraints) =>
        ProtocolEnvelopeValidator.IsIdentifier(constraints.ScenarioId) &&
        constraints.VersionIsValid() &&
        constraints.ScenarioSha256.Length == 64 &&
        constraints.ScenarioSha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        constraints.MinimumCashReserve >= 0 &&
        constraints.PerProjectBudget > 0 &&
        constraints.MaximumActiveProjects is >= 1 and <= 16 &&
        constraints.AllowedModes.Count is > 0 and <= 8 &&
        constraints.AllowedCargo.Count is > 0 and <= 8 &&
        constraints.AllowedTools.Count > 0 &&
        constraints.AllowedTools.Count <= RoadToolCatalog.All.Count &&
        constraints.AllowedModes.All(mode => mode is { Length: > 0 and <= 32 }) &&
        constraints.AllowedCargo.All(cargo => cargo is { Length: > 0 and <= 32 }) &&
        constraints.AllowedTools.All(RoadToolCatalog.IsToolIdentifier);

    private static bool VersionIsValid(this ScenarioActionConstraintContext constraints) =>
        constraints.ScenarioVersion.Length is >= 5 and <= 32 &&
        constraints.ScenarioVersion.All(character => (character is >= '0' and <= '9') || character == '.');

    private static bool TryGetLong(JsonElement arguments, string propertyName, out long value)
    {
        value = 0;
        return arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out value) &&
            value >= 0;
    }

    private static bool TryGetString(JsonElement arguments, string propertyName, out string? value)
    {
        value = null;
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is { Length: > 0 and <= 32 };
    }
}
