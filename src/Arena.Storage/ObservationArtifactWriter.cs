using System.Text;
using System.Text.Json;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Storage;

/// <summary>
/// Appends durable, canonical observation/event records under one validated run
/// root. The writer intentionally accepts only typed public contracts, never a
/// raw provider body or a host path.
/// </summary>
public sealed class ObservationArtifactWriter : IDisposable
{
    public const string ObservationsFileName = "observations.ndjson";
    public const string GameEventsFileName = "game-events.ndjson";
    public const string DecisionsFileName = "decisions.ndjson";
    public const string ProviderUsageFileName = "provider-usage.ndjson";
    public const string ActionsFileName = "actions.ndjson";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly HashSet<string> _persistedEventIds = new(StringComparer.Ordinal);
    private readonly RunPathPolicy _paths;
    private readonly string _runId;
    private bool _disposed;

    public ObservationArtifactWriter(RunPathPolicy paths, string runId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!ProtocolEnvelopeValidator.IsIdentifier(runId))
        {
            throw new ArgumentException("The run identifier is invalid.", nameof(runId));
        }

        _paths = paths;
        _runId = runId;

        /* A finalized run must have the complete artifact shape even when a
         * particular public stream has no entries. Create the bounded set of
         * files up front so a missing event, action, or provider call is
         * distinguishable from a missing artifact. */
        foreach (string path in ArtifactPaths)
        {
            CreateArtifactFile(path);
        }
    }

    public string ObservationsPath => _paths.Resolve(ObservationsFileName);

    public string GameEventsPath => _paths.Resolve(GameEventsFileName);

    public string DecisionsPath => _paths.Resolve(DecisionsFileName);

    public string ProviderUsagePath => _paths.Resolve(ProviderUsageFileName);

    public string ActionsPath => _paths.Resolve(ActionsFileName);

    private IEnumerable<string> ArtifactPaths =>
    [
        ObservationsPath,
        GameEventsPath,
        DecisionsPath,
        ProviderUsagePath,
        ActionsPath,
    ];

    public Task AppendObservationAsync(ObservationBuildRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!string.Equals(record.Snapshot.RunId, _runId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The observation belongs to a different run.", nameof(record));
        }

        RecordedObservation artifact = new()
        {
            SchemaVersion = ContractVersions.ObservationV1,
            RunId = _runId,
            ObservationSha256 = record.Sha256,
            ReplayObservationSha256 = record.ReplaySha256,
            Observation = record.Snapshot,
        };
        return AppendAsync(ObservationsPath, artifact, cancellationToken);
    }

    public async Task AppendEventAsync(NormalizedGameEvent eventEntry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventEntry);
        RecordedGameEvent artifact = new()
        {
            SchemaVersion = ContractVersions.ObservationV1,
            RunId = _runId,
            Event = eventEntry,
        };

        byte[] line = CreateCanonicalLine(artifact);
        ThrowIfDisposed();
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            /* Every ArenaGS snapshot includes a bounded recent-event window.
             * The same event must remain observable on later snapshots without
             * becoming a second historical event in the durable NDJSON stream.
             * Keep this ledger at the writer boundary so every caller gets the
             * same save/load-safe behaviour. */
            if (_persistedEventIds.Contains(eventEntry.EventId))
            {
                return;
            }

            await WriteLineAsync(GameEventsPath, line, cancellationToken);
            _persistedEventIds.Add(eventEntry.EventId);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task AppendDecisionAsync(RecordedDecision record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        EnsureRun(record.RunId, nameof(record));
        return AppendAsync(DecisionsPath, record, cancellationToken);
    }

    public Task AppendProviderUsageAsync(ProviderUsageRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        EnsureRun(record.RunId, nameof(record));
        return AppendAsync(ProviderUsagePath, record, cancellationToken);
    }

    public Task AppendActionAsync(RecordedAction record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        EnsureRun(record.RunId, nameof(record));
        return AppendAsync(ActionsPath, record, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task AppendAsync<T>(string path, T record, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        byte[] line = CreateCanonicalLine(record);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await WriteLineAsync(path, line, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static byte[] CreateCanonicalLine<T>(T record)
    {
        JsonElement element = JsonSerializer.SerializeToElement(record);
        byte[] canonical = CanonicalJson.Serialize(element);
        byte[] line = new byte[canonical.Length + 1];
        Buffer.BlockCopy(canonical, 0, line, 0, canonical.Length);
        line[^1] = (byte)'\n';
        return line;
    }

    private async Task WriteLineAsync(string path, byte[] line, CancellationToken cancellationToken)
    {
        _paths.EnsureSafePath(path);
        await using FileStream stream = new(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(line, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private void CreateArtifactFile(string path)
    {
        _paths.EnsureSafePath(path);
        using FileStream stream = new(
            path,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void EnsureRun(string runId, string parameterName)
    {
        if (!string.Equals(runId, _runId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The artifact belongs to a different run.", parameterName);
        }
    }
}

/// <summary>
/// A small adapter avoids coupling storage to the orchestrator's richer result
/// record while retaining the exact snapshot/hash pair that was sent to a
/// provider.
/// </summary>
public sealed record ObservationBuildRecord(ObservationSnapshot Snapshot, string Sha256, string ReplaySha256);
