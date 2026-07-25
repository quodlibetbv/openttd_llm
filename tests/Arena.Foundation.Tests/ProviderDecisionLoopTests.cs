using System.Text.Json;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Providers;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class ProviderDecisionLoopTests
{
    [Fact]
    public async Task RetriesOnceOnlyForSchemaInvalidOutput()
    {
        CorrectingProvider provider = new();

        ProviderDecisionLoopResult result = await ProviderDecisionLoop.GetDecisionAsync(
            provider,
            CreateRequest(),
            new ProviderDecisionLoopOptions(1),
            CancellationToken.None);

        Assert.True(result.FinalResult.IsSuccess);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(0, provider.Requests[0].SchemaCorrectionAttempt);
        Assert.Equal(1, provider.Requests[1].SchemaCorrectionAttempt);
        Assert.Equal(2, result.Attempts.Count);
    }

    [Fact]
    public async Task DoesNotRetryAuthenticationFailures()
    {
        FailingProvider provider = new(ArenaErrorCodes.ProviderAuthenticationFailed);

        ProviderDecisionLoopResult result = await ProviderDecisionLoop.GetDecisionAsync(
            provider,
            CreateRequest(),
            new ProviderDecisionLoopOptions(1),
            CancellationToken.None);

        Assert.False(result.FinalResult.IsSuccess);
        Assert.Single(provider.Requests);
    }

    private static ModelRequest CreateRequest()
    {
        using JsonDocument observation = JsonDocument.Parse("{\"schema_version\":\"1.0\"}");
        return new ModelRequest
        {
            RunId = "run-0001",
            DecisionId = "decision-0001",
            ObservationHash = new string('a', 64),
            Observation = observation.RootElement.Clone(),
            AvailableTools = ["wait"],
            RemainingModelCalls = 1,
            RemainingOutputTokens = 100,
        };
    }

    private sealed class CorrectingProvider : IModelProvider
    {
        public ProviderDescriptor Descriptor { get; } = new("fixture", "1.0", true);

        public List<ModelRequest> Requests { get; } = [];

        public Task<ProviderDecisionResult> GetDecisionAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (request.SchemaCorrectionAttempt == 0)
            {
                return Task.FromResult(ProviderDecisionResult.Failed(new ArenaError(
                    ArenaErrorCodes.ProviderSchemaMismatch,
                    "The fixture response is invalid.",
                    "fixture invalid output",
                    true),
                    ProviderUsage.Empty));
            }

            using JsonDocument arguments = JsonDocument.Parse("{\"game_days\":30}");
            return Task.FromResult(ProviderDecisionResult.Succeeded(new ModelDecision
            {
                DecisionId = "decision-0001",
                PublicSummary = "Wait for the bounded review interval.",
                Observations = ["The corrective fixture now satisfies the common contract."],
                Actions =
                [
                    new ModelAction
                    {
                        Tool = "wait",
                        Arguments = arguments.RootElement.Clone(),
                    },
                ],
                NextReviewGameDays = 30,
            }, ProviderUsage.Empty));
        }
    }

    private sealed class FailingProvider(string errorCode) : IModelProvider
    {
        public ProviderDescriptor Descriptor { get; } = new("fixture", "1.0", true);

        public List<ModelRequest> Requests { get; } = [];

        public Task<ProviderDecisionResult> GetDecisionAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(ProviderDecisionResult.Failed(new ArenaError(
                errorCode,
                "The fixture request failed.",
                "fixture failure",
                false),
                ProviderUsage.Empty));
        }
    }
}
