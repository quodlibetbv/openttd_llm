using System.Text;
using System.Text.Json;

namespace OpenTtd.ModelArena.Obs;

public static class ArenaObsSceneRequirements
{
    public static IReadOnlyList<string> RequiredSceneNames { get; } =
    [
        "Arena - Starting",
        "Arena - Wide",
        "Arena - Medium",
        "Arena - Close",
        "Arena - Results",
        "Arena - Failure",
    ];

    public static IReadOnlyList<string> RequiredSourceNames { get; } =
    [
        "Arena-Wide",
        "Arena-Medium",
        "Arena-Close",
        "Arena-Sidebar",
        "Arena-Results",
    ];

    public static ObsSceneTemplateValidation ValidateInventory(ObsSceneInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        List<string> missing = [];
        foreach (string sceneName in RequiredSceneNames)
        {
            if (!inventory.SceneSources.ContainsKey(sceneName))
            {
                missing.Add($"scene:{sceneName}");
            }
        }

        HashSet<string> availableSources = inventory.SceneSources
            .Values
            .SelectMany(sourceNames => sourceNames)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string sourceName in RequiredSourceNames)
        {
            if (!availableSources.Contains(sourceName))
            {
                missing.Add($"source:{sourceName}");
            }
        }

        ValidateSceneSource(inventory, "Arena - Wide", "Arena-Wide", missing);
        ValidateSceneSource(inventory, "Arena - Medium", "Arena-Medium", missing);
        ValidateSceneSource(inventory, "Arena - Close", "Arena-Close", missing);
        ValidateSceneSource(inventory, "Arena - Results", "Arena-Results", missing);
        ValidateSceneSource(inventory, "Arena - Failure", "Arena-Results", missing);

        return new ObsSceneTemplateValidation(missing.Count == 0, missing);
    }

    private static void ValidateSceneSource(
        ObsSceneInventory inventory,
        string sceneName,
        string sourceName,
        List<string> missing)
    {
        if (!inventory.SceneSources.TryGetValue(sceneName, out IReadOnlyList<string>? sourceNames) ||
            !sourceNames.Contains(sourceName, StringComparer.Ordinal))
        {
            missing.Add($"scene-source:{sceneName}/{sourceName}");
        }
    }
}

public sealed record ObsSceneInventory(
    IReadOnlyDictionary<string, IReadOnlyList<string>> SceneSources);

public sealed record ObsSceneTemplateValidation(
    bool IsValid,
    IReadOnlyList<string> MissingRequirements);

public sealed record ObsSceneTemplateWriteResult(
    bool Succeeded,
    string? ErrorCode,
    string UserMessage);

public static class ObsSceneTemplateGenerator
{
    public const string TemplateFileName = "Arena-Scene-Collection.template.json";

    private const long MaximumTemplateBytes = 256 * 1024;
    private const int MaximumSceneCount = 100;
    private const int MaximumSourceCount = 100;
    private const int MaximumSourcesPerScene = 50;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions TemplateJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static Task<ObsSceneTemplateWriteResult> WriteAsync(
        string runtimeRoot,
        string outputPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
            EnsureSafeRuntimePath(runtimeRoot, outputPath);
            string fullPath = Path.GetFullPath(outputPath);
            string? parentDirectory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(parentDirectory))
            {
                throw new ArgumentException("The OBS template path has no parent directory.", nameof(outputPath));
            }

            Directory.CreateDirectory(parentDirectory);
            string template = JsonSerializer.Serialize(CreateTemplate(), TemplateJsonOptions) + Environment.NewLine;
            if (!File.Exists(fullPath) ||
                new FileInfo(fullPath).Length > MaximumTemplateBytes ||
                !string.Equals(File.ReadAllText(fullPath), template, StringComparison.Ordinal))
            {
                File.WriteAllText(fullPath, template, Utf8WithoutBom);
            }

            return Task.FromResult(new ObsSceneTemplateWriteResult(
                true,
                null,
                "OBS scene template generated."));
        }
        catch (IOException)
        {
            return Task.FromResult(new ObsSceneTemplateWriteResult(
                false,
                "ARENA-OBS-TEMPLATE-INVALID",
                "The OBS template could not be written. Verify repository write permissions and rerun bootstrap."));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new ObsSceneTemplateWriteResult(
                false,
                "ARENA-OBS-TEMPLATE-INVALID",
                "The OBS template directory is not writable. Grant repository write access and rerun bootstrap."));
        }
        catch (ArgumentException)
        {
            return Task.FromResult(new ObsSceneTemplateWriteResult(
                false,
                "ARENA-OBS-TEMPLATE-INVALID",
                "The OBS template path is invalid. Restore the repository-local runtime path and rerun bootstrap."));
        }
    }

    public static ObsSceneTemplateValidation ValidateFile(string runtimeRoot, string templatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templatePath);
        try
        {
            EnsureSafeRuntimePath(runtimeRoot, templatePath);
        }
        catch (ArgumentException)
        {
            return new ObsSceneTemplateValidation(false, ["template-path"]);
        }
        catch (IOException)
        {
            return new ObsSceneTemplateValidation(false, ["template-path"]);
        }
        catch (UnauthorizedAccessException)
        {
            return new ObsSceneTemplateValidation(false, ["template-path"]);
        }

        if (!File.Exists(templatePath))
        {
            return new ObsSceneTemplateValidation(false, ["template-file"]);
        }

        try
        {
            FileInfo fileInfo = new(templatePath);
            if (fileInfo.Length > MaximumTemplateBytes)
            {
                return new ObsSceneTemplateValidation(false, ["template-too-large"]);
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(templatePath));
            if (!document.RootElement.TryGetProperty("scenes", out JsonElement scenesElement) ||
                scenesElement.ValueKind != JsonValueKind.Array)
            {
                return new ObsSceneTemplateValidation(false, ["template-scenes"]);
            }

            if (!document.RootElement.TryGetProperty("sources", out JsonElement sourceDefinitionsElement) ||
                sourceDefinitionsElement.ValueKind != JsonValueKind.Array)
            {
                return new ObsSceneTemplateValidation(false, ["template-sources"]);
            }

            if (scenesElement.GetArrayLength() > MaximumSceneCount ||
                sourceDefinitionsElement.GetArrayLength() > MaximumSourceCount)
            {
                return new ObsSceneTemplateValidation(false, ["template-entry-limit"]);
            }

            HashSet<string> definedSources = sourceDefinitionsElement
                .EnumerateArray()
                .Where(source => source.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String)
                .Select(source => source.GetProperty("name").GetString())
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            List<string> definitionMissing = ArenaObsSceneRequirements.RequiredSourceNames
                .Where(sourceName => !definedSources.Contains(sourceName))
                .Select(sourceName => $"template-source:{sourceName}")
                .ToList();

            Dictionary<string, IReadOnlyList<string>> inventory = new(StringComparer.Ordinal);
            foreach (JsonElement scene in scenesElement.EnumerateArray())
            {
                if (!scene.TryGetProperty("name", out JsonElement nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String ||
                    !scene.TryGetProperty("sources", out JsonElement sourcesElement) ||
                    sourcesElement.ValueKind != JsonValueKind.Array)
                {
                    return new ObsSceneTemplateValidation(false, ["template-scene-shape"]);
                }

                if (sourcesElement.GetArrayLength() > MaximumSourcesPerScene)
                {
                    return new ObsSceneTemplateValidation(false, ["template-scene-source-limit"]);
                }

                string? name = nameElement.GetString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    return new ObsSceneTemplateValidation(false, ["template-scene-name"]);
                }

                string[] sources = sourcesElement
                    .EnumerateArray()
                    .Where(source => source.ValueKind == JsonValueKind.String)
                    .Select(source => source.GetString())
                    .OfType<string>()
                    .ToArray();
                inventory[name] = sources;
            }

            ObsSceneTemplateValidation inventoryValidation = ArenaObsSceneRequirements.ValidateInventory(new ObsSceneInventory(inventory));
            IReadOnlyList<string> missing = inventoryValidation.MissingRequirements
                .Concat(definitionMissing)
                .ToArray();
            return new ObsSceneTemplateValidation(missing.Count == 0, missing);
        }
        catch (JsonException)
        {
            return new ObsSceneTemplateValidation(false, ["template-json"]);
        }
        catch (IOException)
        {
            return new ObsSceneTemplateValidation(false, ["template-read"]);
        }
        catch (UnauthorizedAccessException)
        {
            return new ObsSceneTemplateValidation(false, ["template-read"]);
        }
    }

    private static ObsSceneCollectionTemplate CreateTemplate() =>
        new(
            1,
            "OpenTTD Model Arena",
            new ObsCanvasTemplate(2560, 1440, 60),
            [
                new ObsSourceTemplate("Arena-Wide", "window_capture", true),
                new ObsSourceTemplate("Arena-Medium", "window_capture", true),
                new ObsSourceTemplate("Arena-Close", "window_capture", true),
                new ObsSourceTemplate("Arena-Sidebar", "browser_source", true),
                new ObsSourceTemplate("Arena-Results", "browser_or_media_source", true),
                new ObsSourceTemplate("Arena-Audio", "application_audio_capture", false),
            ],
            [
                new ObsSceneTemplate("Arena - Starting", ["Arena-Wide", "Arena-Sidebar"]),
                new ObsSceneTemplate("Arena - Wide", ["Arena-Wide", "Arena-Sidebar"]),
                new ObsSceneTemplate("Arena - Medium", ["Arena-Medium", "Arena-Sidebar"]),
                new ObsSceneTemplate("Arena - Close", ["Arena-Close", "Arena-Sidebar"]),
                new ObsSceneTemplate("Arena - Results", ["Arena-Results"]),
                new ObsSceneTemplate("Arena - Failure", ["Arena-Results"]),
            ]);

    private static void EnsureSafeRuntimePath(string runtimeRoot, string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        string normalizedRoot = Path.GetFullPath(runtimeRoot);
        string normalizedCandidate = Path.GetFullPath(candidate);
        if (!Directory.Exists(normalizedRoot) || !IsWithinRoot(normalizedRoot, normalizedCandidate))
        {
            throw new ArgumentException("The OBS template path must remain below the existing runtime root.", nameof(candidate));
        }

        if ((File.GetAttributes(normalizedRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The OBS template path must not traverse a symbolic link or junction.");
        }

        string relativePath = Path.GetRelativePath(normalizedRoot, normalizedCandidate);
        string currentPath = normalizedRoot;
        foreach (string segment in relativePath.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
            {
                continue;
            }

            if ((File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The OBS template path must not traverse a symbolic link or junction.");
            }
        }
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        string normalizedRoot = Path.GetFullPath(root);
        string normalizedCandidate = Path.GetFullPath(candidate);
        string rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ObsSceneCollectionTemplate(
        int TemplateVersion,
        string CollectionName,
        ObsCanvasTemplate Canvas,
        IReadOnlyList<ObsSourceTemplate> Sources,
        IReadOnlyList<ObsSceneTemplate> Scenes);

    private sealed record ObsCanvasTemplate(int Width, int Height, int FramesPerSecond);

    private sealed record ObsSourceTemplate(string Name, string Kind, bool Required);

    private sealed record ObsSceneTemplate(string Name, IReadOnlyList<string> Sources);
}
