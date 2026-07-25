using OpenTtd.ModelArena.Contracts;
using System.Text.Json;

namespace OpenTtd.ModelArena.Providers;

public sealed class ReplayModelProvider : IModelProvider
{
    private readonly ReplayFixture _fixture;
    private int _nextStepIndex;

    public ReplayModelProvider(ReplayFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        _fixture = fixture;
    }

    public ProviderDescriptor Descriptor { get; } = new(
        ProviderId: "replay",
        AdapterVersion: "1.0",
        SupportsStructuredOutput: true);

    public Task<ProviderDecisionResult> GetDecisionAsync(
        ModelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        int index = Interlocked.Increment(ref _nextStepIndex) - 1;
        if (index >= _fixture.Steps.Count)
        {
            return Task.FromResult(ProviderDecisionResult.Failed(
                new ArenaError(
                    ArenaErrorCodes.ProviderReplayExhausted,
                    "The replay fixture has no decision left for this request.",
                    $"Requested replay step {index} but fixture has {_fixture.Steps.Count} steps.",
                    false),
                EmptyUsage()));
        }

        ReplayStep step = _fixture.Steps[index];
        string replayHash = request.ReplayObservationHash ?? request.ObservationHash;
        if (!string.Equals(
                replayHash,
                step.ExpectedObservationSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ProviderDecisionResult.Failed(
                new ArenaError(
                    ArenaErrorCodes.ProviderReplayObservationMismatch,
                    "The replay observation does not match the recorded fixture.",
                    "Replay execution stopped before an incompatible decision could be emitted.",
                    false),
                EmptyUsage()));
        }

        JsonElement decisionJson = JsonSerializer.SerializeToElement(step.Decision, ObservationJsonContext.Default.ModelDecision);
        ModelDecisionValidationResult validation = ModelDecisionValidator.ParseAndValidate(
            decisionJson,
            request.AvailableTools.ToHashSet(StringComparer.Ordinal),
            request.MaximumActions);
        if (!validation.IsValid || validation.Decision is null)
        {
            return Task.FromResult(ProviderDecisionResult.Failed(
                validation.Error!,
                EmptyUsage()));
        }

        ProviderUsage usage = new(
            step.Usage.InputTokens,
            step.Usage.OutputTokens,
            TimeSpan.FromMilliseconds(step.Usage.LatencyMilliseconds),
            ProviderRequestId: null,
            EstimatedCost: null);

        return Task.FromResult(ProviderDecisionResult.Succeeded(validation.Decision, usage));
    }

    private static ProviderUsage EmptyUsage() => ProviderUsage.Empty;
}
