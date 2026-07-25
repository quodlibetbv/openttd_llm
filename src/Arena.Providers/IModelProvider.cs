using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Providers;

public interface IModelProvider
{
    ProviderDescriptor Descriptor { get; }

    Task<ProviderDecisionResult> GetDecisionAsync(
        ModelRequest request,
        CancellationToken cancellationToken);
}

public sealed record ProviderDescriptor(
    string ProviderId,
    string AdapterVersion,
    bool SupportsStructuredOutput);

public sealed record ProviderUsage(
    long InputTokens,
    long OutputTokens,
    TimeSpan Latency,
    string? ProviderRequestId,
    decimal? EstimatedCost)
{
    public static ProviderUsage Empty { get; } = new(
        InputTokens: 0,
        OutputTokens: 0,
        Latency: TimeSpan.Zero,
        ProviderRequestId: null,
        EstimatedCost: null);
}

public sealed record ProviderDecisionResult
{
    public ModelDecision? Decision { get; init; }

    public required ProviderUsage Usage { get; init; }

    public ArenaError? Error { get; init; }

    public bool IsSuccess => Decision is not null && Error is null;

    public static ProviderDecisionResult Succeeded(ModelDecision decision, ProviderUsage usage) =>
        new()
        {
            Decision = decision,
            Usage = usage,
        };

    public static ProviderDecisionResult Failed(ArenaError error, ProviderUsage usage) =>
        new()
        {
            Error = error,
            Usage = usage,
        };
}
