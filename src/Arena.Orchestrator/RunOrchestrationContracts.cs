using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Orchestrator;

public interface IArenaOrchestrator
{
    Task<RunPreparationResult> PrepareAsync(
        RunPreparationRequest request,
        CancellationToken cancellationToken);
}

public sealed record RunPreparationRequest(
    string ScenarioPath,
    string ProviderId,
    string ModelId,
    string CredentialReference);

public sealed record RunPreparationResult(
    bool IsReady,
    string? RunId,
    ArenaError? Error);
