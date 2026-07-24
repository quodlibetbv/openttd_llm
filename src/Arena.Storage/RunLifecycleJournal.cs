using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Storage;

public sealed record InterruptedRunClassification(
    ArenaRunState LastState,
    ArenaRunExitReason SuggestedExitReason);

/// <summary>
/// Appends fsync'd lifecycle records so an interrupted host has enough evidence
/// to distinguish a completed run from an incomplete one.
/// </summary>
public sealed class RunLifecycleJournal : IDisposable
{
    public const string FileName = "lifecycle.ndjson";

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _runId;
    private readonly RunPathPolicy _paths;
    private ArenaRunState? _lastState;

    public RunLifecycleJournal(string runId, RunPathPolicy paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _runId = runId;
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        Path = _paths.Resolve(FileName);
    }

    public string Path { get; }

    public void Dispose()
    {
        _writeLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public Task InitializeAsync(DateTimeOffset occurredUtc, CancellationToken cancellationToken) =>
        AppendAsync(ArenaRunState.Created, occurredUtc, null, null, null, null, cancellationToken);

    public Task TransitionAsync(
        ArenaRunState state,
        DateTimeOffset occurredUtc,
        string? componentId,
        ArenaRunExitReason? exitReason,
        string? errorCode,
        string? detail,
        CancellationToken cancellationToken)
    {
        if (_lastState is null)
        {
            throw new InvalidOperationException("The lifecycle journal must be initialized before transitions are appended.");
        }

        if (!IsAllowedTransition(_lastState.Value, state))
        {
            throw new InvalidOperationException(
                $"Invalid run lifecycle transition from {_lastState.Value} to {state}.");
        }

        if (state is ArenaRunState.Completed or ArenaRunState.Failed or ArenaRunState.Cancelled)
        {
            ArgumentNullException.ThrowIfNull(exitReason);
        }
        else if (exitReason is not null)
        {
            throw new InvalidOperationException("Only terminal lifecycle records may include an exit reason.");
        }

        return AppendAsync(state, occurredUtc, componentId, exitReason, errorCode, detail, cancellationToken);
    }

    public static RunLifecycleEvent? ReadLatest(string runDirectory)
    {
        RunPathPolicy paths = new(runDirectory);
        string journalPath = paths.Resolve(FileName);
        if (!File.Exists(journalPath))
        {
            return null;
        }

        string? lastLine = File.ReadLines(journalPath)
            .LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
        return lastLine is null
            ? null
            : JsonSerializer.Deserialize<RunLifecycleEvent>(lastLine, JsonOptions);
    }

    public static InterruptedRunClassification? ClassifyInterrupted(string runDirectory)
    {
        RunLifecycleEvent? latest = ReadLatest(runDirectory);
        if (latest is null || latest.State is ArenaRunState.Completed or ArenaRunState.Failed or ArenaRunState.Cancelled)
        {
            return null;
        }

        ArenaRunExitReason reason = latest.State switch
        {
            ArenaRunState.Created or ArenaRunState.Preparing => ArenaRunExitReason.PreparationFailed,
            ArenaRunState.StartingServer or ArenaRunState.WaitingForGameScript or ArenaRunState.StartingClients => ArenaRunExitReason.StartupTimedOut,
            ArenaRunState.Finalizing => ArenaRunExitReason.FinalizationFailed,
            _ => ArenaRunExitReason.ServerExited,
        };
        return new InterruptedRunClassification(latest.State, reason);
    }

    private async Task AppendAsync(
        ArenaRunState state,
        DateTimeOffset occurredUtc,
        string? componentId,
        ArenaRunExitReason? exitReason,
        string? errorCode,
        string? detail,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunLifecycleEvent entry = new()
        {
            SchemaVersion = ContractVersions.RunLifecycleV1,
            RunId = _runId,
            OccurredUtc = occurredUtc,
            State = state,
            ComponentId = componentId,
            ExitReason = exitReason,
            ErrorCode = errorCode,
            Detail = detail,
        };
        byte[] line = Utf8WithoutBom.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            _paths.EnsureSafePath(Path);
            await using FileStream stream = new(
                Path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough | FileOptions.Asynchronous);
            await stream.WriteAsync(line, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
            _lastState = state;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static bool IsAllowedTransition(ArenaRunState previous, ArenaRunState next) =>
        previous switch
        {
            ArenaRunState.Created => next == ArenaRunState.Preparing,
            ArenaRunState.Preparing => next is ArenaRunState.StartingServer or ArenaRunState.Finalizing,
            ArenaRunState.StartingServer => next is ArenaRunState.WaitingForGameScript or ArenaRunState.Finalizing,
            // A protocol-only run, such as the Phase 03 bridge smoke, has no
            // spectator-client stage between GameScript readiness and Ready.
            ArenaRunState.WaitingForGameScript => next is ArenaRunState.StartingClients or ArenaRunState.Ready or ArenaRunState.Finalizing,
            ArenaRunState.StartingClients => next is ArenaRunState.Ready or ArenaRunState.Finalizing,
            ArenaRunState.Ready => next is ArenaRunState.Running or ArenaRunState.Finalizing,
            ArenaRunState.Running => next == ArenaRunState.Finalizing,
            ArenaRunState.Finalizing => next is ArenaRunState.Completed or ArenaRunState.Failed or ArenaRunState.Cancelled,
            _ => false,
        };
}
