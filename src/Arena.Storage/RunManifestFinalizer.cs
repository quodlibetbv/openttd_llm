using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Storage;

public sealed record RunManifestDraft(
    string RunId,
    DateTimeOffset CreatedUtc,
    string ApplicationVersion,
    string GitCommit,
    string Provider,
    string Model,
    string ScenarioId,
    string ScenarioVersion,
    ContractVersionsUsed ContractVersions,
    BenchmarkInputHashes BenchmarkInputHashes);

public sealed record RunManifestFinalizationResult(
    RunManifest Manifest,
    string ManifestPath,
    string SealPath,
    string ManifestSha256);

/// <summary>
/// Seals a completed benchmark run. The manifest records every benchmark input
/// and its selected evidence artifacts; a separate checksum seals the manifest
/// itself without making it self-referential.
/// </summary>
public static class RunManifestFinalizer
{
    public const string ManifestFileName = "run-manifest.json";
    public const string SealFileName = "run-manifest.sha256";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static async Task<RunManifestFinalizationResult> FinalizeAsync(
        RunPathPolicy paths,
        RunManifestDraft draft,
        IReadOnlyCollection<string> artifactRelativePaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(artifactRelativePaths);
        ValidateDraft(draft);
        IReadOnlyList<ArtifactHash> artifacts = await HashArtifactsAsync(paths, artifactRelativePaths, cancellationToken);
        ValidateInputHashes(draft.BenchmarkInputHashes, artifacts);
        RunManifest manifest = new()
        {
            SchemaVersion = ContractVersions.RunManifestV1,
            RunId = draft.RunId,
            CreatedUtc = draft.CreatedUtc.ToUniversalTime(),
            ApplicationVersion = draft.ApplicationVersion,
            GitCommit = draft.GitCommit,
            Provider = draft.Provider,
            Model = draft.Model,
            ScenarioId = draft.ScenarioId,
            ScenarioVersion = draft.ScenarioVersion,
            ContractVersions = draft.ContractVersions,
            BenchmarkInputHashes = draft.BenchmarkInputHashes,
            ArtifactHashes = artifacts,
        };
        string manifestPath = paths.Resolve(ManifestFileName);
        await WriteCanonicalCreateNewAsync(manifestPath, paths, manifest, cancellationToken);
        JsonElement manifestJson = JsonSerializer.SerializeToElement(manifest);
        string manifestSha256 = CanonicalJson.ComputeSha256(manifestJson);
        string sealPath = paths.Resolve(SealFileName);
        await WriteTextCreateNewAsync(sealPath, paths, manifestSha256 + "  " + ManifestFileName + "\n", cancellationToken);
        return new RunManifestFinalizationResult(manifest, manifestPath, sealPath, manifestSha256);
    }

    private static async Task<IReadOnlyList<ArtifactHash>> HashArtifactsAsync(
        RunPathPolicy paths,
        IReadOnlyCollection<string> artifactRelativePaths,
        CancellationToken cancellationToken)
    {
        HashSet<string> normalized = new(StringComparer.Ordinal);
        foreach (string relativePath in artifactRelativePaths)
        {
            if (string.IsNullOrWhiteSpace(relativePath) ||
                string.Equals(relativePath, ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(relativePath, SealFileName, StringComparison.OrdinalIgnoreCase) ||
                !normalized.Add(relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)))
            {
                throw new InvalidOperationException($"{ArenaErrorCodes.ArtifactVerificationFailed}: manifest artifacts must be unique safe relative paths.");
            }
        }

        List<ArtifactHash> hashes = [];
        foreach (string relativePath in normalized.OrderBy(path => path, StringComparer.Ordinal))
        {
            string path = paths.Resolve(relativePath);
            paths.EnsureSafePath(path);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"{ArenaErrorCodes.RunArtifactMissing}: a required benchmark artifact is absent.");
            }

            FileInfo info = new(path);
            string sha256 = await ComputeFileSha256Async(path, cancellationToken);
            hashes.Add(new ArtifactHash
            {
                RelativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                Sha256 = sha256,
                ByteLength = info.Length,
            });
        }

        return hashes;
    }

    private static void ValidateDraft(RunManifestDraft draft)
    {
        if (!ProtocolEnvelopeValidator.IsIdentifier(draft.RunId) ||
            !ProtocolEnvelopeValidator.IsIdentifier(draft.ScenarioId) ||
            !IsSemanticVersion(draft.ScenarioVersion) ||
            !IsSemanticVersion(draft.ApplicationVersion) ||
            !IsSha256(draft.GitCommit) ||
            string.IsNullOrWhiteSpace(draft.Provider) || draft.Provider.Length > 80 ||
            string.IsNullOrWhiteSpace(draft.Model) || draft.Model.Length > 160)
        {
            throw new ArgumentException("The run-manifest draft has invalid immutable identity metadata.", nameof(draft));
        }
    }

    private static void ValidateInputHashes(BenchmarkInputHashes hashes, IReadOnlyList<ArtifactHash> artifacts)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        Dictionary<string, string> byPath = artifacts.ToDictionary(entry => entry.RelativePath, entry => entry.Sha256, StringComparer.Ordinal);
        (string Path, string Hash)[] expected =
        [
            ("input/starting-save.sav", hashes.StartingSaveSha256),
            ("input/content-manifest.json", hashes.ContentManifestSha256),
            ("input/game-settings.cfg", hashes.GameSettingsSha256),
            ("input/scenario.yaml", hashes.ScenarioSha256),
            ("input/prompt-template.txt", hashes.PromptTemplateSha256),
            ("input/tool-contracts.json", hashes.ToolContractSha256),
            ("input/schemas/observation.v1.json", hashes.ObservationSchemaSha256),
            ("input/schemas/action-request.v1.json", hashes.ActionSchemaSha256),
            ("input/schemas/score.v1.json", hashes.ScoreSchemaSha256),
            ("input/schemas/protocol-envelope.v1.json", hashes.ProtocolSchemaSha256),
            ("input/retry-policy.json", hashes.RetryPolicySha256),
            ("input/end-condition.json", hashes.EndConditionSha256),
        ];
        foreach ((string path, string hash) in expected)
        {
            if (!IsSha256(hash) || !byPath.TryGetValue(path, out string? actual) || !string.Equals(hash, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{ArenaErrorCodes.ArtifactVerificationFailed}: a benchmark input hash does not match its captured artifact.");
            }
        }
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task WriteCanonicalCreateNewAsync<T>(
        string path,
        RunPathPolicy paths,
        T value,
        CancellationToken cancellationToken)
    {
        paths.EnsureSafePath(path);
        byte[] canonical = CanonicalJson.Serialize(JsonSerializer.SerializeToElement(value));
        byte[] content = new byte[canonical.Length + 1];
        Buffer.BlockCopy(canonical, 0, content, 0, canonical.Length);
        content[^1] = (byte)'\n';
        await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4_096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static async Task WriteTextCreateNewAsync(
        string path,
        RunPathPolicy paths,
        string content,
        CancellationToken cancellationToken)
    {
        paths.EnsureSafePath(path);
        await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4_096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        byte[] bytes = Utf8WithoutBom.GetBytes(content);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static bool IsSemanticVersion(string value) =>
        Regex.IsMatch(value, "^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
