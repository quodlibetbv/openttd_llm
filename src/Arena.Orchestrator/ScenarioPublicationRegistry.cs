using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Orchestrator;

public sealed record PublishedScenarioEntry
{
    [JsonPropertyName("scenario_id")]
    public required string ScenarioId { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }
}

public sealed record ScenarioPublicationCatalog
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("published_scenarios")]
    public required IReadOnlyList<PublishedScenarioEntry> PublishedScenarios { get; init; }
}

public sealed record ScenarioPublicationResult(bool Succeeded, string? ErrorCode, string Detail)
{
    public static ScenarioPublicationResult Success(string detail) => new(true, null, detail);

    public static ScenarioPublicationResult Failure(string detail) =>
        new(false, ArenaErrorCodes.ScenarioPublicationConflict, detail);
}

/// <summary>
/// Records published scenario fingerprints. A published identifier/version is
/// a content address: changing its bytes is rejected, while a newer semantic
/// version is a distinct publication.
/// </summary>
public static class ScenarioPublicationRegistry
{
    public const string DefaultRelativePath = "scenarios/published-scenarios.v1.json";
    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static async Task<ScenarioPublicationCatalog> LoadAsync(
        string repositoryRoot,
        string? catalogPath,
        CancellationToken cancellationToken)
    {
        string path = ResolveCatalogPath(repositoryRoot, catalogPath);
        if (!File.Exists(path))
        {
            return new ScenarioPublicationCatalog
            {
                SchemaVersion = ContractVersions.ScenarioV1,
                PublishedScenarios = [],
            };
        }

        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            ScenarioPublicationCatalog? catalog = await JsonSerializer.DeserializeAsync<ScenarioPublicationCatalog>(stream, StrictJsonOptions, cancellationToken);
            if (catalog is null || !string.Equals(catalog.SchemaVersion, ContractVersions.ScenarioV1, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The publication catalog has no supported schema version.");
            }

            ValidateCatalog(catalog);
            return catalog;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.ScenarioInvalid}: the scenario publication catalog is not a closed supported JSON contract.");
        }
    }

    public static ScenarioPublicationResult Validate(ScenarioDocument document, ScenarioPublicationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);
        PublishedScenarioEntry? existing = catalog.PublishedScenarios.SingleOrDefault(entry =>
            string.Equals(entry.ScenarioId, document.Scenario.ScenarioId, StringComparison.Ordinal) &&
            string.Equals(entry.Version, document.Scenario.Version, StringComparison.Ordinal));
        if (existing is null)
        {
            return ScenarioPublicationResult.Success("The scenario version is not yet published.");
        }

        return string.Equals(existing.Sha256, document.Sha256, StringComparison.OrdinalIgnoreCase)
            ? ScenarioPublicationResult.Success("The scenario matches its published immutable fingerprint.")
            : ScenarioPublicationResult.Failure("The published scenario content changed without a version change.");
    }

    public static async Task<ScenarioPublicationResult> PublishAsync(
        string repositoryRoot,
        string? catalogPath,
        ScenarioDocument document,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        string path = ResolveCatalogPath(repositoryRoot, catalogPath);
        ScenarioPublicationCatalog catalog = await LoadAsync(repositoryRoot, path, cancellationToken);
        ScenarioPublicationResult validation = Validate(document, catalog);
        if (!validation.Succeeded)
        {
            return validation;
        }

        if (catalog.PublishedScenarios.Any(entry =>
                string.Equals(entry.ScenarioId, document.Scenario.ScenarioId, StringComparison.Ordinal) &&
                string.Equals(entry.Version, document.Scenario.Version, StringComparison.Ordinal)))
        {
            return ScenarioPublicationResult.Success("The scenario version is already published with its immutable fingerprint.");
        }

        if (catalog.PublishedScenarios
            .Where(entry => string.Equals(entry.ScenarioId, document.Scenario.ScenarioId, StringComparison.Ordinal))
            .Any(entry => CompareSemanticVersion(document.Scenario.Version, entry.Version) <= 0))
        {
            return ScenarioPublicationResult.Failure("A new publication must increment the scenario semantic version.");
        }

        List<PublishedScenarioEntry> entries = catalog.PublishedScenarios
            .Append(new PublishedScenarioEntry
            {
                ScenarioId = document.Scenario.ScenarioId,
                Version = document.Scenario.Version,
                Sha256 = document.Sha256,
            })
            .OrderBy(entry => entry.ScenarioId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Version, StringComparer.Ordinal)
            .ToList();
        ScenarioPublicationCatalog updated = new()
        {
            SchemaVersion = ContractVersions.ScenarioV1,
            PublishedScenarios = entries,
        };
        await WriteAtomicallyAsync(path, updated, cancellationToken);
        return ScenarioPublicationResult.Success("The scenario version was published with an immutable SHA-256 fingerprint.");
    }

    private static void ValidateCatalog(ScenarioPublicationCatalog catalog)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (PublishedScenarioEntry entry in catalog.PublishedScenarios)
        {
            if (!ProtocolEnvelopeValidator.IsIdentifier(entry.ScenarioId) ||
                !IsSemanticVersion(entry.Version) ||
                entry.Sha256.Length != 64 ||
                entry.Sha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
                !keys.Add(entry.ScenarioId + "@" + entry.Version))
            {
                throw new InvalidOperationException($"{ArenaErrorCodes.ScenarioInvalid}: the publication catalog contains an invalid or duplicate entry.");
            }
        }
    }

    private static async Task WriteAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.ScenarioInvalid}: the publication catalog has no parent directory.");
        }

        Directory.CreateDirectory(parent);
        string temporaryPath = Path.Combine(parent, ".published-scenarios.pending-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            string json = JsonSerializer.Serialize(value, WriteJsonOptions) + Environment.NewLine;
            await File.WriteAllTextAsync(temporaryPath, json, Encoding.UTF8, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string ResolveCatalogPath(string repositoryRoot, string? catalogPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.ScenarioInvalid}: the repository root is unavailable.");
        }

        string root = Path.GetFullPath(repositoryRoot);
        string candidate = Path.IsPathRooted(catalogPath)
            ? Path.GetFullPath(catalogPath)
            : Path.GetFullPath(Path.Combine(root, catalogPath ?? DefaultRelativePath));
        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
            !candidate.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.ScenarioInvalid}: publication catalogs must be JSON files below the repository root.");
        }

        return candidate;
    }

    private static bool IsSemanticVersion(string value)
    {
        string[] parts = value.Split('.', StringSplitOptions.None);
        return parts.Length == 3 && parts.All(part => int.TryParse(part, out int parsed) && parsed >= 0);
    }

    private static int CompareSemanticVersion(string left, string right)
    {
        string[] leftParts = left.Split('.', StringSplitOptions.None);
        string[] rightParts = right.Split('.', StringSplitOptions.None);
        for (int index = 0; index < 3; index++)
        {
            int comparison = int.Parse(leftParts[index], System.Globalization.CultureInfo.InvariantCulture)
                .CompareTo(int.Parse(rightParts[index], System.Globalization.CultureInfo.InvariantCulture));
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }
}
