using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Providers;

namespace OpenTtd.ModelArena.Orchestrator;

/// <summary>
/// Creates the exact provider-neutral request handed to every adapter. Keeping
/// this factory outside adapter code makes byte-equivalence auditable and
/// testable for a normalized observation and scenario context.
/// </summary>
public static class ProviderRequestFactory
{
    public static ModelRequest Create(
        ObservationBuildResult observation,
        ProviderDecisionExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(options);
        return new ModelRequest
        {
            RunId = options.ObservationContext.RunId,
            DecisionId = options.DecisionId,
            ObservationHash = observation.Sha256,
            ReplayObservationHash = observation.ReplaySha256,
            Observation = observation.CanonicalJson,
            AvailableTools = options.ObservationContext.AllowedTools.OrderBy(tool => tool, StringComparer.Ordinal).ToArray(),
            RemainingModelCalls = options.ObservationContext.RemainingModelCalls,
            RemainingOutputTokens = options.ObservationContext.RemainingOutputTokens,
            MaximumActions = options.MaximumActions,
            PromptTemplateVersion = ArenaPromptTemplate.Version,
            PromptTemplateSha256 = ArenaPromptTemplate.Sha256,
        };
    }
}
