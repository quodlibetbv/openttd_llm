using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Orchestrator;

/// <summary>
/// A narrowly-scoped result for a supervisor-owned save/load operation. It
/// intentionally reveals no console command, server path, or credential to an
/// extension's provider-facing code.
/// </summary>
public sealed record Phase03SaveLoadResult(
    bool Succeeded,
    string? ErrorCode,
    string Detail);

/// <summary>
/// Lets trusted phase extensions verify persistence using a generated,
/// run-local checkpoint. This is not a model capability: only the Phase 03
/// service constructs it after starting the isolated dedicated server.
/// </summary>
public interface IPhase03SaveLoadController
{
    Task<Phase03SaveLoadResult> SaveAndReloadAsync(
        string checkpointName,
        CancellationToken cancellationToken);
}

internal sealed class Phase03SaveLoadController : IPhase03SaveLoadController
{
    private static readonly TimeSpan FilePollInterval = TimeSpan.FromMilliseconds(100);
    private readonly RunPathPolicy _paths;
    private readonly string _serverSaveDirectory;
    private readonly IManagedArenaProcess _server;
    private readonly IOpenTtdConsoleBridge _consoleBridge;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _timeProvider;

    public Phase03SaveLoadController(
        RunPathPolicy paths,
        string serverSaveDirectory,
        IManagedArenaProcess server,
        IOpenTtdConsoleBridge consoleBridge,
        TimeSpan timeout,
        TimeProvider timeProvider)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _serverSaveDirectory = string.IsNullOrWhiteSpace(serverSaveDirectory)
            ? throw new ArgumentException("The isolated server save directory is required.", nameof(serverSaveDirectory))
            : serverSaveDirectory;
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _consoleBridge = consoleBridge ?? throw new ArgumentNullException(nameof(consoleBridge));
        _timeout = timeout > TimeSpan.Zero
            ? timeout
            : throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<Phase03SaveLoadResult> SaveAndReloadAsync(
        string checkpointName,
        CancellationToken cancellationToken)
    {
        OpenTtdConsoleCommand save;
        OpenTtdConsoleCommand load;
        try
        {
            save = OpenTtdConsoleCommand.Save(checkpointName);
            load = OpenTtdConsoleCommand.Load(checkpointName);
        }
        catch (ArgumentException)
        {
            return new Phase03SaveLoadResult(
                false,
                ArenaErrorCodes.ActionConstraintViolation,
                "The generated checkpoint name did not satisfy the dedicated-server safety policy.");
        }

        if (_server.HasExited)
        {
            return new Phase03SaveLoadResult(
                false,
                ArenaErrorCodes.RunServerExited,
                "The isolated OpenTTD server exited before the checkpoint could be saved.");
        }

        try
        {
            await _consoleBridge.SendAsync(_server.ProcessId, save, cancellationToken);
            string serverSavePath = Path.Combine(_serverSaveDirectory, checkpointName + ".sav");
            if (!await WaitForStableFileAsync(serverSavePath, cancellationToken))
            {
                return new Phase03SaveLoadResult(
                    false,
                    ArenaErrorCodes.RunArtifactMissing,
                    "OpenTTD did not finish the requested checkpoint save before the bounded timeout.");
            }

            _paths.CreateDirectory("checkpoints");
            string artifactPath = _paths.Resolve(Path.Combine("checkpoints", checkpointName + ".sav"));
            File.Copy(serverSavePath, artifactPath, overwrite: false);
            await _consoleBridge.SendAsync(_server.ProcessId, load, cancellationToken);
            return new Phase03SaveLoadResult(
                true,
                null,
                "The isolated server saved and began loading the generated run-local checkpoint.");
        }
        catch (OpenTtdConsoleControlException)
        {
            return new Phase03SaveLoadResult(
                false,
                ArenaErrorCodes.RunConsoleControlFailed,
                "The dedicated OpenTTD console did not accept the supervisor checkpoint command.");
        }
        catch (IOException)
        {
            return new Phase03SaveLoadResult(
                false,
                ArenaErrorCodes.RunArtifactMissing,
                "The isolated checkpoint artifact could not be written or read safely.");
        }
    }

    private async Task<bool> WaitForStableFileAsync(string path, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = _timeProvider.GetUtcNow().Add(_timeout);
        long? priorLength = null;
        using PeriodicTimer timer = new(FilePollInterval, _timeProvider);
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileInfo file = new(path);
                if (file.Exists && file.Length > 0)
                {
                    if (priorLength == file.Length)
                    {
                        return true;
                    }

                    priorLength = file.Length;
                }
            }
            catch (IOException)
            {
                priorLength = null;
            }

            if (!await timer.WaitForNextTickAsync(cancellationToken))
            {
                break;
            }
        }

        return false;
    }
}
