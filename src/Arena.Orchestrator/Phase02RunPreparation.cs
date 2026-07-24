using System.Security.Cryptography;
using System.Text;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Orchestrator;

public sealed record ArenaSpectatorDefinition(
    string ComponentId,
    string ClientName,
    string StableWindowTitle);

public static class Phase02SmokeDefaults
{
    public const string GameScriptReadyMarker = "ARENA_PHASE02_GAMESCRIPT_READY";
    public const string ModelProxyReadyMarker = "ARENA_PHASE02_MODEL_PROXY_READY";
    public const string StartingSaveTemplateVersion = "phase-02-smoke-v1";
    public const uint StartingSaveSeed = 20260723;

    public static IReadOnlyList<ArenaSpectatorDefinition> Spectators { get; } =
    [
        new ArenaSpectatorDefinition("spectator-wide", "Arena-Wide", "Arena - Wide"),
        new ArenaSpectatorDefinition("spectator-medium", "Arena-Medium", "Arena - Medium"),
        new ArenaSpectatorDefinition("spectator-close", "Arena-Close", "Arena - Close"),
    ];
}

public sealed record Phase02RunLayout(
    RunDirectoryAllocation Allocation,
    string InputDirectory,
    string StartingSavePath,
    string ServerDirectory,
    string ServerConfigurationPath,
    string ServerSaveDirectory,
    string CheckpointsDirectory,
    string FinalSavePath,
    string ComponentLogsDirectory,
    string ResultPath,
    IReadOnlyDictionary<string, Phase02SpectatorWorkspace> Spectators);

public sealed record Phase02ServerWorkspace(
    string ComponentId,
    string WorkingDirectory,
    string ConfigurationPath,
    string SaveDirectory,
    string StandardOutputLogPath,
    string StandardErrorLogPath);

public sealed record Phase02SpectatorWorkspace(
    ArenaSpectatorDefinition Definition,
    string WorkingDirectory,
    string ConfigurationPath,
    string StandardOutputLogPath,
    string StandardErrorLogPath);

/// <summary>
/// Materializes a run-local OpenTTD profile from the immutable runtime templates.
/// OpenTTD writes only to these copies; it never receives a runtime template or
/// cached starting save as a writable launch target.
/// </summary>
public static class Phase02RunPreparation
{
    private const string StartingSaveFileName = "starting-save.sav";
    private const string FinalSaveFileName = "final-save.sav";
    private const string ResultFileName = "run-result.json";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static Phase02RunLayout CreateLayout(RunDirectoryAllocation allocation)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        RunPathPolicy paths = allocation.Paths;
        string inputDirectory = paths.CreateDirectory("input");
        string serverDirectory = paths.CreateDirectory("server");
        string serverSaveDirectory = paths.CreateDirectory(Path.Combine("server", "save"));
        string checkpointsDirectory = paths.CreateDirectory("checkpoints");
        string componentLogsDirectory = paths.CreateDirectory("component-logs");
        Dictionary<string, Phase02SpectatorWorkspace> spectators = new(StringComparer.Ordinal);
        foreach (ArenaSpectatorDefinition definition in Phase02SmokeDefaults.Spectators)
        {
            string workingDirectory = paths.CreateDirectory(Path.Combine("spectators", definition.ComponentId));
            spectators.Add(
                definition.ComponentId,
                new Phase02SpectatorWorkspace(
                    definition,
                    workingDirectory,
                    paths.Resolve(Path.Combine("spectators", definition.ComponentId, ArenaRuntimeLayout.OpenTtdConfigurationFileName)),
                    paths.Resolve(Path.Combine("component-logs", definition.ComponentId + ".stdout.log")),
                    paths.Resolve(Path.Combine("component-logs", definition.ComponentId + ".stderr.log"))));
        }

        return new Phase02RunLayout(
            allocation,
            inputDirectory,
            paths.Resolve(Path.Combine("input", StartingSaveFileName)),
            serverDirectory,
            paths.Resolve(Path.Combine("server", ArenaRuntimeLayout.OpenTtdConfigurationFileName)),
            serverSaveDirectory,
            checkpointsDirectory,
            paths.Resolve(FinalSaveFileName),
            componentLogsDirectory,
            paths.Resolve(ResultFileName),
            spectators);
    }

    public static async Task<Phase02ServerWorkspace> PrepareServerWorkspaceAsync(
        ArenaLocalConfiguration configuration,
        RunPathPolicy paths,
        string relativeWorkingDirectory,
        string componentId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeWorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        cancellationToken.ThrowIfCancellationRequested();

        string workingDirectory = paths.CreateDirectory(relativeWorkingDirectory);
        string configurationPath = paths.Resolve(Path.Combine(relativeWorkingDirectory, ArenaRuntimeLayout.OpenTtdConfigurationFileName));
        string saveDirectory = paths.CreateDirectory(Path.Combine(relativeWorkingDirectory, "save"));
        string componentLogs = paths.CreateDirectory("component-logs");
        string standardOutputLogPath = paths.Resolve(Path.Combine("component-logs", componentId + ".stdout.log"));
        string standardErrorLogPath = paths.Resolve(Path.Combine("component-logs", componentId + ".stderr.log"));
        RuntimeTemplatePaths templates = GetRuntimeTemplatePaths(configuration);

        await CopyFileAsync(templates.ServerConfiguration, configurationPath, paths, cancellationToken);
        await CopyFileAsync(
            templates.PrivateConfiguration,
            paths.Resolve(Path.Combine(relativeWorkingDirectory, ArenaRuntimeLayout.PrivateConfigurationFileName)),
            paths,
            cancellationToken);
        await CopyDirectoryAsync(
            templates.GamePackageDirectory,
            paths.Resolve(Path.Combine(relativeWorkingDirectory, ArenaRuntimeLayout.GameDirectoryName, ArenaRuntimeLayout.ArenaGameScriptName)),
            paths,
            cancellationToken);
        await CopyDirectoryAsync(
            templates.AiPackageDirectory,
            paths.Resolve(Path.Combine(relativeWorkingDirectory, ArenaRuntimeLayout.AiDirectoryName, ArenaRuntimeLayout.ModelProxyAiName)),
            paths,
            cancellationToken);
        await InitializeComponentLogsAsync(standardOutputLogPath, standardErrorLogPath, paths, cancellationToken);

        return new Phase02ServerWorkspace(
            componentId,
            workingDirectory,
            configurationPath,
            saveDirectory,
            standardOutputLogPath,
            standardErrorLogPath);
    }

    public static async Task PrepareRunWorkspacesAsync(
        ArenaLocalConfiguration configuration,
        Phase02RunLayout layout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(layout);
        RuntimeTemplatePaths templates = GetRuntimeTemplatePaths(configuration);

        await CopyFileAsync(templates.ServerConfiguration, layout.ServerConfigurationPath, layout.Allocation.Paths, cancellationToken);
        await CopyFileAsync(
            templates.PrivateConfiguration,
            layout.Allocation.Paths.Resolve(Path.Combine("server", ArenaRuntimeLayout.PrivateConfigurationFileName)),
            layout.Allocation.Paths,
            cancellationToken);
        await CopyDirectoryAsync(
            templates.GamePackageDirectory,
            layout.Allocation.Paths.Resolve(Path.Combine("server", ArenaRuntimeLayout.GameDirectoryName, ArenaRuntimeLayout.ArenaGameScriptName)),
            layout.Allocation.Paths,
            cancellationToken);
        await CopyDirectoryAsync(
            templates.AiPackageDirectory,
            layout.Allocation.Paths.Resolve(Path.Combine("server", ArenaRuntimeLayout.AiDirectoryName, ArenaRuntimeLayout.ModelProxyAiName)),
            layout.Allocation.Paths,
            cancellationToken);
        await InitializeComponentLogsAsync(
            layout.Allocation.Paths.Resolve(Path.Combine("component-logs", "server.stdout.log")),
            layout.Allocation.Paths.Resolve(Path.Combine("component-logs", "server.stderr.log")),
            layout.Allocation.Paths,
            cancellationToken);

        string spectatorTemplate = await File.ReadAllTextAsync(templates.SpectatorConfiguration, cancellationToken);
        if (CountOccurrences(spectatorTemplate, "{{client_name}}") != 1)
        {
            throw new InvalidOperationException(
                $"{ArenaErrorCodes.RunPreparationFailed}: spectator configuration template has no unambiguous client-name placeholder.");
        }

        foreach (Phase02SpectatorWorkspace spectator in layout.Spectators.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsValidClientName(spectator.Definition.ClientName))
            {
                throw new InvalidOperationException(
                    $"{ArenaErrorCodes.RunPreparationFailed}: generated spectator client name is invalid.");
            }

            string rendered = spectatorTemplate.Replace("{{client_name}}", spectator.Definition.ClientName, StringComparison.Ordinal);
            await WriteTextAsync(spectator.ConfigurationPath, rendered, layout.Allocation.Paths, cancellationToken);
            await InitializeComponentLogsAsync(
                spectator.StandardOutputLogPath,
                spectator.StandardErrorLogPath,
                layout.Allocation.Paths,
                cancellationToken);
        }
    }

    public static string GetStartingSaveCachePath(ArenaLocalConfiguration configuration)
    {
        RuntimeTemplatePaths templates = GetRuntimeTemplatePaths(configuration);
        string serverTemplate = File.ReadAllText(templates.ServerConfiguration);
        string contentManifest = File.ReadAllText(templates.ContentManifest);
        byte[] descriptor = Utf8WithoutBom.GetBytes(
            Phase02SmokeDefaults.StartingSaveTemplateVersion + "\n" +
            Phase02SmokeDefaults.StartingSaveSeed.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" +
            serverTemplate + "\n" + contentManifest);
        string hash = Convert.ToHexString(SHA256.HashData(descriptor)).ToLowerInvariant()[..16];
        string cacheDirectory = Path.Combine(configuration.Runtime.Root, ArenaRuntimeLayout.CacheDirectoryName, "phase-02-smoke");
        return Path.Combine(cacheDirectory, $"starting-save-{hash}.sav");
    }

    public static RuntimeTemplatePaths GetRuntimeTemplatePaths(ArenaLocalConfiguration configuration)
    {
        string runtimeRoot = Path.GetFullPath(configuration.Runtime.Root);
        string openTtdRoot = Path.GetDirectoryName(Path.GetFullPath(configuration.OpenTtd.Executable))
            ?? throw new InvalidOperationException($"{ArenaErrorCodes.RunPreparationFailed}: OpenTTD executable path has no parent directory.");
        if (!IsWithinRoot(runtimeRoot, openTtdRoot))
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.RunPreparationFailed}: OpenTTD executable is outside the configured runtime root.");
        }

        RuntimeTemplatePaths paths = new(
            Path.GetFullPath(configuration.OpenTtd.ServerConfiguration),
            Path.GetFullPath(configuration.OpenTtd.SpectatorConfiguration),
            Path.Combine(openTtdRoot, ArenaRuntimeLayout.PrivateConfigurationFileName),
            Path.Combine(openTtdRoot, ArenaRuntimeLayout.GameDirectoryName, ArenaRuntimeLayout.ArenaGameScriptName),
            Path.Combine(openTtdRoot, ArenaRuntimeLayout.AiDirectoryName, ArenaRuntimeLayout.ModelProxyAiName),
            Path.Combine(openTtdRoot, ArenaRuntimeLayout.ContentManifestFileName));
        foreach (string path in paths.AllPaths)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                throw new InvalidOperationException($"{ArenaErrorCodes.RunPreparationFailed}: a required immutable OpenTTD template is missing.");
            }
        }

        return paths;
    }

    private static async Task InitializeComponentLogsAsync(
        string standardOutputPath,
        string standardErrorPath,
        RunPathPolicy paths,
        CancellationToken cancellationToken)
    {
        await WriteTextAsync(standardOutputPath, "# OpenTTD Model Arena component standard output\n", paths, cancellationToken);
        await WriteTextAsync(standardErrorPath, "# OpenTTD Model Arena component standard error\n", paths, cancellationToken);
    }

    private static async Task CopyFileAsync(
        string source,
        string destination,
        RunPathPolicy destinationPaths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        destinationPaths.EnsureSafePath(destination);
        string? parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.RunPreparationFailed}: generated artifact has no parent directory.");
        }

        Directory.CreateDirectory(parent);
        await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private static async Task CopyDirectoryAsync(
        string sourceDirectory,
        string destinationDirectory,
        RunPathPolicy destinationPaths,
        CancellationToken cancellationToken)
    {
        foreach (string source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"{ArenaErrorCodes.RunPreparationFailed}: immutable OpenTTD templates may not contain symbolic links or junctions.");
            }

            string relative = Path.GetRelativePath(sourceDirectory, source);
            string destination = Path.Combine(destinationDirectory, relative);
            await CopyFileAsync(source, destination, destinationPaths, cancellationToken);
        }
    }

    private static async Task WriteTextAsync(
        string path,
        string contents,
        RunPathPolicy paths,
        CancellationToken cancellationToken)
    {
        paths.EnsureSafePath(path);
        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.RunPreparationFailed}: generated artifact has no parent directory.");
        }

        Directory.CreateDirectory(parent);
        await File.WriteAllTextAsync(path, contents, Utf8WithoutBom, cancellationToken);
    }

    private static int CountOccurrences(string value, string marker)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        return count;
    }

    private static bool IsValidClientName(string value) =>
        value.Length is >= 3 and <= 32 &&
        value.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

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
}

public sealed record RuntimeTemplatePaths(
    string ServerConfiguration,
    string SpectatorConfiguration,
    string PrivateConfiguration,
    string GamePackageDirectory,
    string AiPackageDirectory,
    string ContentManifest)
{
    public IReadOnlyList<string> AllPaths { get; } =
    [
        ServerConfiguration,
        SpectatorConfiguration,
        PrivateConfiguration,
        GamePackageDirectory,
        AiPackageDirectory,
        ContentManifest,
    ];
}
