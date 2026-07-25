using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Storage;

public sealed record RunVerificationResult(
    bool Succeeded,
    string? ErrorCode,
    string Detail,
    int VerifiedArtifactCount)
{
    public static RunVerificationResult Failure(string detail) =>
        new(false, ArenaErrorCodes.ArtifactVerificationFailed, detail, 0);
}

/// <summary>
/// Independently checks a Phase 07 run seal, every captured input and evidence
/// hash, and the score-to-final-metric relationship. It never calls a provider
/// or opens an OpenTTD process.
/// </summary>
public static class RunVerifier
{
    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly string[] RequiredArtifacts =
    [
        ObservationArtifactWriter.ObservationsFileName,
        ObservationArtifactWriter.GameEventsFileName,
        ObservationArtifactWriter.DecisionsFileName,
        ObservationArtifactWriter.ProviderUsageFileName,
        ObservationArtifactWriter.ActionsFileName,
        ObservationArtifactWriter.MetricsFileName,
        BenchmarkArtifactStore.FinalMetricsFileName,
        BenchmarkArtifactStore.ScoreFileName,
        "final-save.sav",
        "input/starting-save.sav",
        "input/scenario.yaml",
    ];

    public static async Task<RunVerificationResult> VerifyAsync(string runDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runDirectory) || !Directory.Exists(runDirectory))
        {
            return RunVerificationResult.Failure("The run directory does not exist.");
        }

        try
        {
            RunPathPolicy paths = new(runDirectory);
            string manifestPath = paths.Resolve(RunManifestFinalizer.ManifestFileName);
            string sealPath = paths.Resolve(RunManifestFinalizer.SealFileName);
            if (!File.Exists(manifestPath) || !File.Exists(sealPath))
            {
                return RunVerificationResult.Failure("The immutable run manifest or its checksum seal is absent.");
            }

            byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
            RunManifest? manifest;
            JsonElement manifestJson;
            try
            {
                using JsonDocument document = JsonDocument.Parse(manifestBytes);
                manifestJson = document.RootElement.Clone();
                manifest = JsonSerializer.Deserialize<RunManifest>(manifestBytes, StrictJsonOptions);
            }
            catch (JsonException)
            {
                return RunVerificationResult.Failure("The run manifest is not a closed supported JSON contract.");
            }

            if (manifest is null ||
                !string.Equals(manifest.SchemaVersion, ContractVersions.RunManifestV1, StringComparison.Ordinal) ||
                !ProtocolEnvelopeValidator.IsIdentifier(manifest.RunId))
            {
                return RunVerificationResult.Failure("The run manifest identity is invalid.");
            }

            string? sealedHash = await ReadSealAsync(sealPath, cancellationToken);
            string actualManifestHash = CanonicalJson.ComputeSha256(manifestJson);
            if (!string.Equals(sealedHash, actualManifestHash, StringComparison.OrdinalIgnoreCase))
            {
                return RunVerificationResult.Failure("The run manifest checksum seal does not match its canonical content.");
            }

            HashSet<string> artifactPaths = new(StringComparer.Ordinal);
            foreach (ArtifactHash artifact in manifest.ArtifactHashes)
            {
                if (!IsSafeRelativePath(artifact.RelativePath) || !artifactPaths.Add(artifact.RelativePath) ||
                    !IsSha256(artifact.Sha256) || artifact.ByteLength < 0)
                {
                    return RunVerificationResult.Failure("The run manifest contains an invalid artifact hash entry.");
                }

                string fullPath = paths.Resolve(artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath))
                {
                    return RunVerificationResult.Failure("A manifest-listed artifact is absent.");
                }

                FileInfo info = new(fullPath);
                string actualHash = await ComputeFileSha256Async(fullPath, cancellationToken);
                if (info.Length != artifact.ByteLength || !string.Equals(actualHash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return RunVerificationResult.Failure("A manifest-listed artifact was altered after finalization.");
                }
            }

            if (RequiredArtifacts.Any(required => !artifactPaths.Contains(required)))
            {
                return RunVerificationResult.Failure("The manifest omits a required Phase 07 evidence artifact.");
            }

            if (!ValidateInputHashes(manifest.BenchmarkInputHashes, manifest.ArtifactHashes))
            {
                return RunVerificationResult.Failure("A benchmark-defining input does not match its declared manifest hash.");
            }

            BenchmarkMetricSnapshot finalMetrics = await BenchmarkArtifactStore.ReadFinalMetricsAsync(paths, cancellationToken);
            ScoreResult score = await BenchmarkArtifactStore.ReadScoreAsync(paths, cancellationToken);
            string finalMetricsHash = CanonicalJson.ComputeSha256(JsonSerializer.SerializeToElement(finalMetrics));
            if (!string.Equals(score.FinalMetricsSha256, finalMetricsHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(score.RunId, manifest.RunId, StringComparison.Ordinal) ||
                !string.Equals(finalMetrics.RunId, manifest.RunId, StringComparison.Ordinal) ||
                !string.Equals(score.ScenarioId, manifest.ScenarioId, StringComparison.Ordinal) ||
                !string.Equals(score.ScenarioVersion, manifest.ScenarioVersion, StringComparison.Ordinal))
            {
                return RunVerificationResult.Failure("The stored score does not identify the sealed final metrics and scenario.");
            }

            return new RunVerificationResult(true, null, "The run manifest, evidence artifacts, final metrics, and stored score are internally verified.", manifest.ArtifactHashes.Count);
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith(ArenaErrorCodes.PathOutsideRunRoot, StringComparison.Ordinal))
        {
            return new RunVerificationResult(false, ArenaErrorCodes.PathOutsideRunRoot, "The run verifier rejected a path outside the active run root.", 0);
        }
        catch (IOException)
        {
            return RunVerificationResult.Failure("A run artifact could not be read safely.");
        }
        catch (InvalidOperationException)
        {
            return RunVerificationResult.Failure("A run artifact does not satisfy the supported verification contract.");
        }
    }

    private static bool ValidateInputHashes(BenchmarkInputHashes hashes, IReadOnlyList<ArtifactHash> artifacts)
    {
        Dictionary<string, string> byPath = artifacts.ToDictionary(artifact => artifact.RelativePath, artifact => artifact.Sha256, StringComparer.Ordinal);
        return Matches("input/starting-save.sav", hashes.StartingSaveSha256) &&
            Matches("input/content-manifest.json", hashes.ContentManifestSha256) &&
            Matches("input/game-settings.cfg", hashes.GameSettingsSha256) &&
            Matches("input/scenario.yaml", hashes.ScenarioSha256) &&
            Matches("input/prompt-template.txt", hashes.PromptTemplateSha256) &&
            Matches("input/tool-contracts.json", hashes.ToolContractSha256) &&
            Matches("input/schemas/observation.v1.json", hashes.ObservationSchemaSha256) &&
            Matches("input/schemas/action-request.v1.json", hashes.ActionSchemaSha256) &&
            Matches("input/schemas/score.v1.json", hashes.ScoreSchemaSha256) &&
            Matches("input/schemas/protocol-envelope.v1.json", hashes.ProtocolSchemaSha256) &&
            Matches("input/retry-policy.json", hashes.RetryPolicySha256) &&
            Matches("input/end-condition.json", hashes.EndConditionSha256);

        bool Matches(string path, string expected) =>
            IsSha256(expected) && byPath.TryGetValue(path, out string? actual) && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> ReadSealAsync(string sealPath, CancellationToken cancellationToken)
    {
        string content = await File.ReadAllTextAsync(sealPath, cancellationToken);
        string expected = RunManifestFinalizer.ManifestFileName;
        string[] pieces = content.TrimEnd('\r', '\n').Split("  ", StringSplitOptions.None);
        return pieces.Length == 2 && string.Equals(pieces[1], expected, StringComparison.Ordinal) && IsSha256(pieces[0])
            ? pieces[0]
            : null;
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsSafeRelativePath(string path) =>
        path.Length is > 0 and <= 260 &&
        !Path.IsPathRooted(path) &&
        !path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or "..");

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
