using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Providers;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Orchestrator;

public sealed record BenchmarkInputCapture(
    BenchmarkInputHashes Hashes,
    IReadOnlyList<string> ArtifactRelativePaths);

/// <summary>
/// Captures every input that defines benchmark semantics inside the isolated
/// run root before a score can be calculated. The manifest therefore remains
/// independently verifiable even after repository files later change.
/// </summary>
public static class BenchmarkInputSnapshotter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static async Task<BenchmarkInputCapture> CaptureAsync(
        string repositoryRoot,
        ArenaLocalConfiguration configuration,
        RunPathPolicy paths,
        ScenarioDocument scenario,
        string startingSavePath,
        string gameSettingsPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(scenario);
        paths.CreateDirectory("input");
        paths.CreateDirectory("input/schemas");
        RuntimeTemplatePaths runtime = Phase02RunPreparation.GetRuntimeTemplatePaths(configuration);
        IReadOnlyList<(string Source, string Relative)> copies =
        [
            (startingSavePath, "input/starting-save.sav"),
            (runtime.ContentManifest, "input/content-manifest.json"),
            (gameSettingsPath, "input/game-settings.cfg"),
            (scenario.Path, "input/scenario.yaml"),
            (Path.Combine(repositoryRoot, "schemas", "observations", "observation.v1.json"), "input/schemas/observation.v1.json"),
            (Path.Combine(repositoryRoot, "schemas", "actions", "action-request.v1.json"), "input/schemas/action-request.v1.json"),
            (Path.Combine(repositoryRoot, "schemas", "scores", "score.v1.json"), "input/schemas/score.v1.json"),
            (Path.Combine(repositoryRoot, "schemas", "protocol", "protocol-envelope.v1.json"), "input/schemas/protocol-envelope.v1.json"),
        ];
        foreach ((string source, string relative) in copies)
        {
            await CopyCreateNewAsync(source, paths.Resolve(relative), paths, cancellationToken);
        }

        await WriteCreateNewAsync(paths.Resolve("input/prompt-template.txt"), ArenaPromptTemplate.ManifestText, paths, cancellationToken);
        await WriteBytesCreateNewAsync(
            paths.Resolve("input/tool-contracts.json"),
            CanonicalJson.Serialize(JsonSerializer.SerializeToElement(RoadToolPromptCatalog.AllContracts)),
            paths,
            cancellationToken);
        await WriteBytesCreateNewAsync(
            paths.Resolve("input/retry-policy.json"),
            CanonicalJson.Serialize(JsonSerializer.SerializeToElement(new { maximum_retries = scenario.Scenario.ModelBudget.MaximumRetries })),
            paths,
            cancellationToken);
        await WriteBytesCreateNewAsync(
            paths.Resolve("input/end-condition.json"),
            CanonicalJson.Serialize(JsonSerializer.SerializeToElement(scenario.Scenario.EndCondition)),
            paths,
            cancellationToken);

        string[] artifacts =
        [
            "input/starting-save.sav",
            "input/content-manifest.json",
            "input/game-settings.cfg",
            "input/scenario.yaml",
            "input/prompt-template.txt",
            "input/tool-contracts.json",
            "input/schemas/observation.v1.json",
            "input/schemas/action-request.v1.json",
            "input/schemas/score.v1.json",
            "input/schemas/protocol-envelope.v1.json",
            "input/retry-policy.json",
            "input/end-condition.json",
        ];
        Dictionary<string, string> hashes = new(StringComparer.Ordinal);
        foreach (string relative in artifacts)
        {
            hashes[relative] = await ComputeFileSha256Async(paths.Resolve(relative), cancellationToken);
        }

        if (!string.Equals(hashes["input/scenario.yaml"], scenario.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(hashes["input/prompt-template.txt"], ArenaPromptTemplate.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(hashes["input/tool-contracts.json"], RoadToolPromptCatalog.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.ArtifactVerificationFailed}: a captured benchmark input did not retain its trusted source fingerprint.");
        }

        return new BenchmarkInputCapture(
            new BenchmarkInputHashes
            {
                StartingSaveSha256 = hashes["input/starting-save.sav"],
                ContentManifestSha256 = hashes["input/content-manifest.json"],
                ScenarioSha256 = hashes["input/scenario.yaml"],
                GameSettingsSha256 = hashes["input/game-settings.cfg"],
                PromptTemplateSha256 = hashes["input/prompt-template.txt"],
                ToolContractSha256 = hashes["input/tool-contracts.json"],
                ObservationSchemaSha256 = hashes["input/schemas/observation.v1.json"],
                ActionSchemaSha256 = hashes["input/schemas/action-request.v1.json"],
                ScoreSchemaSha256 = hashes["input/schemas/score.v1.json"],
                ProtocolSchemaSha256 = hashes["input/schemas/protocol-envelope.v1.json"],
                RetryPolicySha256 = hashes["input/retry-policy.json"],
                EndConditionSha256 = hashes["input/end-condition.json"],
            },
            artifacts);
    }

    private static async Task CopyCreateNewAsync(string source, string destination, RunPathPolicy paths, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.RunArtifactMissing}: a benchmark input source is unavailable.");
        }

        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            paths.EnsureSafePath(destination);
            return;
        }

        paths.EnsureSafePath(destination);
        await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private static Task WriteCreateNewAsync(string destination, string content, RunPathPolicy paths, CancellationToken cancellationToken) =>
        WriteBytesCreateNewAsync(destination, Utf8WithoutBom.GetBytes(content), paths, cancellationToken);

    private static async Task WriteBytesCreateNewAsync(string destination, byte[] bytes, RunPathPolicy paths, CancellationToken cancellationToken)
    {
        paths.EnsureSafePath(destination);
        await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4_096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await output.WriteAsync(bytes, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
