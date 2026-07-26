using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Storage;

/// <summary>
/// Writes the immutable terminal scoring inputs as canonical JSON under the
/// validated run root. These files are later sealed by the run manifest.
/// </summary>
public static class BenchmarkArtifactStore
{
    public const string FinalMetricsFileName = "final-metrics.json";
    public const string ScoreFileName = "score.json";
    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task WriteFinalMetricsAsync(
        RunPathPolicy paths,
        BenchmarkMetricSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.Equals(snapshot.SchemaVersion, ContractVersions.MetricV1, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Kind, "final", StringComparison.Ordinal))
        {
            throw new ArgumentException("Only a final v1 metric snapshot can be written as final-metrics.json.", nameof(snapshot));
        }

        await WriteCanonicalCreateNewAsync(paths, FinalMetricsFileName, snapshot, cancellationToken);
    }

    public static Task WriteScoreAsync(
        RunPathPolicy paths,
        ScoreResult score,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(score);
        if (!string.Equals(score.SchemaVersion, ContractVersions.ScoreV1, StringComparison.Ordinal))
        {
            throw new ArgumentException("The score does not use the supported score contract.", nameof(score));
        }

        return WriteCanonicalCreateNewAsync(paths, ScoreFileName, score, cancellationToken);
    }

    public static async Task<BenchmarkMetricSnapshot> ReadFinalMetricsAsync(
        RunPathPolicy paths,
        CancellationToken cancellationToken)
    {
        string path = paths.Resolve(FinalMetricsFileName);
        return await ReadStrictAsync<BenchmarkMetricSnapshot>(path, cancellationToken);
    }

    public static async Task<ScoreResult> ReadScoreAsync(
        RunPathPolicy paths,
        CancellationToken cancellationToken)
    {
        string path = paths.Resolve(ScoreFileName);
        return await ReadStrictAsync<ScoreResult>(path, cancellationToken);
    }

    private static async Task WriteCanonicalCreateNewAsync<T>(
        RunPathPolicy paths,
        string relativePath,
        T value,
        CancellationToken cancellationToken)
    {
        string path = paths.Resolve(relativePath);
        paths.EnsureSafePath(path);
        JsonElement element = JsonSerializer.SerializeToElement(value);
        byte[] bytes = CanonicalJson.Serialize(element);
        byte[] withNewline = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, withNewline, 0, bytes.Length);
        withNewline[^1] = (byte)'\n';
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(withNewline, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<T> ReadStrictAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.RunArtifactMissing}: the required benchmark artifact is absent.");
        }

        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            T? value = await JsonSerializer.DeserializeAsync<T>(stream, StrictJsonOptions, cancellationToken);
            return value ?? throw new InvalidOperationException($"{ArenaErrorCodes.ArtifactVerificationFailed}: the benchmark artifact is empty.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.ArtifactVerificationFailed}: the benchmark artifact is not a closed supported JSON contract.");
        }
    }
}
