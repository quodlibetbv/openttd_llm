using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Obs;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Orchestrator;

public sealed record BootstrapRequest(
    string RepositoryRoot,
    string ArenaConfigurationPath,
    string ProvidersConfigurationPath,
    string? OpenTtdSourceDirectory);

public sealed record BootstrapResult(
    bool Succeeded,
    IReadOnlyList<string> CreatedOrUpdated,
    IReadOnlyList<string> Warnings,
    ArenaError? Error);

public static class BootstrapService
{
    public static async Task<BootstrapResult> RunAsync(
        BootstrapRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RepositoryRoot) || !Directory.Exists(request.RepositoryRoot))
        {
            return Failure("The repository root is missing. Run bootstrap from a complete checkout.");
        }

        try
        {
            string repositoryRoot = Path.GetFullPath(request.RepositoryRoot);
            string arenaConfigurationPath = EnsureRepositoryPath(repositoryRoot, request.ArenaConfigurationPath);
            string providersConfigurationPath = EnsureRepositoryPath(repositoryRoot, request.ProvidersConfigurationPath);
            List<string> createdOrUpdated = [];

            foreach (string directory in new[]
            {
                Path.Combine(repositoryRoot, ".config"),
                Path.Combine(repositoryRoot, "artifacts"),
                Path.Combine(repositoryRoot, "logs"),
            })
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(directory);
            }

            CopyExampleIfMissing(
                Path.Combine(repositoryRoot, ".config", "arena.example.yaml"),
                arenaConfigurationPath,
                "arena local configuration",
                createdOrUpdated);
            CopyExampleIfMissing(
                Path.Combine(repositoryRoot, ".config", "providers.example.yaml"),
                providersConfigurationPath,
                "provider local configuration",
                createdOrUpdated);

            ArenaConfigurationLoadResult configurationResult = await ArenaConfigurationLoader.LoadArenaAsync(
                repositoryRoot,
                arenaConfigurationPath,
                cancellationToken);
            if (!configurationResult.Succeeded || configurationResult.Configuration is null)
            {
                return new BootstrapResult(
                    false,
                    createdOrUpdated,
                    [],
                    new ArenaError(
                        ArenaErrorCodes.ConfigurationInvalid,
                        "The local arena configuration is invalid. Restore the example and move credentials into Windows Credential Manager.",
                        "configuration-validation-failed",
                        false));
            }

            ArenaLocalConfiguration configuration = configurationResult.Configuration;
            RuntimeLayoutResult runtime = await RuntimeLayoutBuilder.PrepareAsync(
                new RuntimeLayoutRequest(
                    repositoryRoot,
                    configuration.Runtime.Root,
                    request.OpenTtdSourceDirectory,
                    configuration.Network.BindAddress,
                    configuration.OpenTtd.AdminPort),
                cancellationToken);
            if (!runtime.Succeeded)
            {
                return new BootstrapResult(false, createdOrUpdated, runtime.Warnings, runtime.Error);
            }

            Directory.CreateDirectory(configuration.Runtime.Runs);
            Directory.CreateDirectory(configuration.Runtime.Recordings);
            createdOrUpdated.AddRange(runtime.CreatedOrUpdated);
            string templatePath = Path.Combine(
                configuration.Runtime.Root,
                ArenaRuntimeLayout.ObsDirectoryName,
                ObsSceneTemplateGenerator.TemplateFileName);
            ObsSceneTemplateWriteResult template = await ObsSceneTemplateGenerator.WriteAsync(
                configuration.Runtime.Root,
                templatePath,
                cancellationToken);
            if (!template.Succeeded)
            {
                return new BootstrapResult(
                    false,
                    createdOrUpdated,
                    runtime.Warnings,
                    new ArenaError(
                        template.ErrorCode ?? ArenaErrorCodes.ObsTemplateInvalid,
                        template.UserMessage,
                        "obs-template-generation-failed",
                        false));
            }

            createdOrUpdated.Add("OBS scene template");
            return new BootstrapResult(true, createdOrUpdated, runtime.Warnings, null);
        }
        catch (IOException exception)
        {
            return Failure("Bootstrap could not create repository-local setup files. Verify repository write permissions and rerun it.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure("Bootstrap could not write repository-local setup files. Grant the repository user write access and rerun it.", exception);
        }
        catch (ArgumentException exception)
        {
            return new BootstrapResult(
                false,
                [],
                [],
                new ArenaError(
                    ArenaErrorCodes.ConfigurationInvalid,
                    "Bootstrap received an unsafe local configuration path. Use the default local configuration paths and rerun it.",
                    exception.GetType().Name,
                    false));
        }
    }

    private static BootstrapResult Failure(string userMessage, Exception? exception = null) =>
        new(
            false,
            [],
            [],
            new ArenaError(
                ArenaErrorCodes.RuntimeLayoutInvalid,
                userMessage,
                exception?.GetType().Name ?? "bootstrap-input-invalid",
                false));

    private static string EnsureRepositoryPath(string repositoryRoot, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(repositoryRoot, path));
        string rootWithSeparator = repositoryRoot.EndsWith(Path.DirectorySeparatorChar)
            ? repositoryRoot
            : repositoryRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Bootstrap configuration must be inside the repository.", nameof(path));
        }

        EnsureNoReparsePoints(repositoryRoot, fullPath);

        string? parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new ArgumentException("Bootstrap configuration has no parent directory.", nameof(path));
        }

        Directory.CreateDirectory(parent);
        return fullPath;
    }

    private static void EnsureNoReparsePoints(string repositoryRoot, string candidate)
    {
        string relativePath = Path.GetRelativePath(repositoryRoot, candidate);
        string currentPath = Path.GetFullPath(repositoryRoot);
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
                throw new ArgumentException(
                    "Bootstrap configuration must not traverse a symbolic link or junction.",
                    nameof(candidate));
            }
        }
    }

    private static void CopyExampleIfMissing(
        string sourcePath,
        string destinationPath,
        string label,
        List<string> createdOrUpdated)
    {
        if (File.Exists(destinationPath))
        {
            return;
        }

        if (!File.Exists(sourcePath))
        {
            throw new IOException($"Missing bootstrap example for {label}.");
        }

        File.Copy(sourcePath, destinationPath, false);
        createdOrUpdated.Add(label);
    }
}
