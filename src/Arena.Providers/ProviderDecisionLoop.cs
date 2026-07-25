using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Providers;

public sealed record ProviderDecisionLoopOptions(int MaximumSchemaCorrectionRetries)
{
    public void Validate()
    {
        if (MaximumSchemaCorrectionRetries is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumSchemaCorrectionRetries), "The Phase 05 correction retry limit must be zero or one.");
        }
    }
}

public sealed record ProviderDecisionLoopResult(
    ProviderDecisionResult FinalResult,
    IReadOnlyList<ProviderUsage> Attempts);

/// <summary>
/// Owns the one bounded corrective retry mandated by the common decision
/// contract. It never grants a provider additional gameplay time: callers keep
/// simulation paused until this loop and downstream action authorization end.
/// </summary>
public sealed class ProviderDecisionLoop
{
    public static async Task<ProviderDecisionLoopResult> GetDecisionAsync(
        IModelProvider provider,
        ModelRequest request,
        ProviderDecisionLoopOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        List<ProviderUsage> attempts = [];
        for (int attempt = 0; attempt <= options.MaximumSchemaCorrectionRetries; attempt++)
        {
            ModelRequest requestAttempt = request with { SchemaCorrectionAttempt = attempt };
            ProviderDecisionResult result = await provider.GetDecisionAsync(requestAttempt, cancellationToken);
            attempts.Add(result.Usage);
            if (result.IsSuccess ||
                !IsCorrectableContractFailure(result.Error?.Code) ||
                attempt == options.MaximumSchemaCorrectionRetries)
            {
                return new ProviderDecisionLoopResult(result, attempts);
            }
        }

        throw new InvalidOperationException("The bounded provider correction loop terminated unexpectedly.");
    }

    private static bool IsCorrectableContractFailure(string? errorCode) =>
        string.Equals(errorCode, ArenaErrorCodes.ProviderInvalidJson, StringComparison.Ordinal) ||
        string.Equals(errorCode, ArenaErrorCodes.ProviderSchemaMismatch, StringComparison.Ordinal) ||
        /* Preserve retry behavior for a persisted Phase 05 artifact created
         * before the JSON/schema split was introduced. */
        string.Equals(errorCode, ArenaErrorCodes.ProviderInvalidOutput, StringComparison.Ordinal);
}
