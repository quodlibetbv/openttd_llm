namespace OpenTtd.ModelArena.Obs;

public interface IObsClient
{
    Task<ObsCommandResult> SwitchSceneAsync(string sceneName, CancellationToken cancellationToken);

    Task<ObsCommandResult> StartRecordingAsync(CancellationToken cancellationToken);

    Task<ObsCommandResult> StopRecordingAsync(CancellationToken cancellationToken);
}

public sealed record ObsCommandResult(
    bool Succeeded,
    string? ErrorCode,
    string? TechnicalMessage);
