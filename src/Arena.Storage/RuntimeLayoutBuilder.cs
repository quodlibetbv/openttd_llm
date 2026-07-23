using System.Security.Cryptography;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Storage;

public static class ArenaRuntimeLayout
{
    public const int GameServerPort = 3979;
    public const string OpenTtdDirectoryName = "openttd";
    public const string GameDirectoryName = "game";
    public const string AiDirectoryName = "ai";
    public const string ArenaGameScriptName = "ArenaGS";
    public const string ModelProxyAiName = "ModelProxyAI";
    public const string ObsDirectoryName = "obs";
    public const string RunsDirectoryName = "runs";
    public const string RecordingsDirectoryName = "recordings";
    public const string CacheDirectoryName = "cache";
    public const string TempDirectoryName = "temp";
    public const string ContentManifestFileName = "content-manifest.json";
    public const string OpenTtdConfigurationFileName = "openttd.cfg";
    public const string PrivateConfigurationFileName = "private.cfg";
    public const string SecretsConfigurationFileName = "secrets.cfg";
    public const string ServerConfigurationFileName = "server.cfg";
    public const string SpectatorConfigurationFileName = "spectator.cfg";
}

public sealed record RuntimeLayoutRequest(
    string RepositoryRoot,
    string RuntimeRoot,
    string? OpenTtdSourceDirectory,
    string BindAddress,
    int AdminPort);

public sealed record RuntimeLayoutResult(
    bool Succeeded,
    IReadOnlyList<string> CreatedOrUpdated,
    IReadOnlyList<string> Warnings,
    ArenaError? Error);

public static class RuntimeLayoutBuilder
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };
    private static readonly HashSet<string> UserMutableOpenTtdDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "autosave",
        "crash",
        "logs",
        "save",
        "screenshot",
    };
    private static readonly HashSet<string> UserMutableOpenTtdFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ArenaRuntimeLayout.OpenTtdConfigurationFileName,
        ArenaRuntimeLayout.PrivateConfigurationFileName,
        ArenaRuntimeLayout.SecretsConfigurationFileName,
        "hotkeys.cfg",
        "hs.dat",
        "windows.cfg",
    };

    public static Task<RuntimeLayoutResult> PrepareAsync(
        RuntimeLayoutRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Prepare(request, cancellationToken));
    }

    private static RuntimeLayoutResult Prepare(RuntimeLayoutRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.RepositoryRoot) ||
                string.IsNullOrWhiteSpace(request.RuntimeRoot) ||
                !IPAddress.TryParse(request.BindAddress, out IPAddress? bindAddress) ||
                !IPAddress.IsLoopback(bindAddress) ||
                request.AdminPort is < 1024 or > 65535 ||
                request.AdminPort == ArenaRuntimeLayout.GameServerPort)
            {
                return Failure("Runtime layout inputs are incomplete or unsafe. Restore the example local configuration and rerun bootstrap.");
            }

            string repositoryRoot = Path.GetFullPath(request.RepositoryRoot);
            string runtimeRoot = Path.GetFullPath(request.RuntimeRoot);
            if (!Directory.Exists(repositoryRoot) || !IsWithinRoot(repositoryRoot, runtimeRoot))
            {
                return Failure("The runtime root must be a repository-owned path. Set runtime.root to a path below this checkout.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(repositoryRoot, runtimeRoot);
            Directory.CreateDirectory(runtimeRoot);

            string openttdRoot = ResolveWithinRoot(runtimeRoot, ArenaRuntimeLayout.OpenTtdDirectoryName);
            string runsRoot = ResolveWithinRoot(runtimeRoot, ArenaRuntimeLayout.RunsDirectoryName);
            string recordingsRoot = ResolveWithinRoot(runtimeRoot, ArenaRuntimeLayout.RecordingsDirectoryName);
            string cacheRoot = ResolveWithinRoot(runtimeRoot, ArenaRuntimeLayout.CacheDirectoryName);
            string tempRoot = ResolveWithinRoot(runtimeRoot, ArenaRuntimeLayout.TempDirectoryName);
            string obsRoot = ResolveWithinRoot(runtimeRoot, ArenaRuntimeLayout.ObsDirectoryName);
            foreach (string directory in new[] { openttdRoot, runsRoot, recordingsRoot, cacheRoot, tempRoot, obsRoot })
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNoReparsePoints(runtimeRoot, directory);
                Directory.CreateDirectory(directory);
            }

            List<string> createdOrUpdated =
            [
                ArenaRuntimeLayout.OpenTtdDirectoryName,
                ArenaRuntimeLayout.RunsDirectoryName,
                ArenaRuntimeLayout.RecordingsDirectoryName,
                ArenaRuntimeLayout.CacheDirectoryName,
                ArenaRuntimeLayout.TempDirectoryName,
                ArenaRuntimeLayout.ObsDirectoryName,
            ];
            List<string> warnings = [];

            if (!string.IsNullOrWhiteSpace(request.OpenTtdSourceDirectory))
            {
                string sourceRoot = Path.GetFullPath(request.OpenTtdSourceDirectory);
                if (!Directory.Exists(sourceRoot))
                {
                    warnings.Add("The supplied OpenTTD source directory was not found. Install OpenTTD or pass a valid -OpenTtdSource path, then rerun bootstrap.");
                }
                else if (IsWithinRoot(sourceRoot, runtimeRoot) || IsWithinRoot(runtimeRoot, sourceRoot))
                {
                    return Failure("The OpenTTD source and isolated runtime must not contain one another. Choose the installed OpenTTD directory outside .runtime.");
                }
                else
                {
                    CopyOpenTtdInstallation(sourceRoot, openttdRoot, cancellationToken);
                    createdOrUpdated.Add("openttd installation files");
                }
            }
            else
            {
                warnings.Add("No OpenTTD installation source was supplied. The isolated layout is ready, but doctor will block until openttd.exe is copied into it.");
            }

            string gameSource = ResolveWithinRoot(repositoryRoot, Path.Combine(
                "openttd",
                ArenaRuntimeLayout.GameDirectoryName,
                ArenaRuntimeLayout.ArenaGameScriptName));
            string aiSource = ResolveWithinRoot(repositoryRoot, Path.Combine(
                "openttd",
                ArenaRuntimeLayout.AiDirectoryName,
                ArenaRuntimeLayout.ModelProxyAiName));
            if (!Directory.Exists(gameSource) || !Directory.Exists(aiSource))
            {
                return Failure("ArenaGS and ModelProxyAI sources are missing from this checkout. Restore the repository packages before bootstrapping.");
            }

            string gameTarget = ResolveWithinRoot(openttdRoot, Path.Combine(
                ArenaRuntimeLayout.GameDirectoryName,
                ArenaRuntimeLayout.ArenaGameScriptName));
            string aiTarget = ResolveWithinRoot(openttdRoot, Path.Combine(
                ArenaRuntimeLayout.AiDirectoryName,
                ArenaRuntimeLayout.ModelProxyAiName));
            ReplaceDirectory(gameSource, gameTarget, openttdRoot, cancellationToken);
            ReplaceDirectory(aiSource, aiTarget, openttdRoot, cancellationToken);
            createdOrUpdated.Add("ArenaGS package");
            createdOrUpdated.Add("ModelProxyAI package");

            string openTtdConfiguration = RenderOpenTtdConfiguration(request.AdminPort);
            string privateConfiguration = RenderPrivateConfiguration(request.BindAddress);
            string serverConfiguration = RenderServerConfiguration(request.AdminPort);
            string spectatorConfiguration = RenderSpectatorConfiguration();
            WriteTextIfChanged(
                ResolveWithinRoot(openttdRoot, ArenaRuntimeLayout.OpenTtdConfigurationFileName),
                openTtdConfiguration,
                openttdRoot);
            WriteTextIfChanged(
                ResolveWithinRoot(openttdRoot, ArenaRuntimeLayout.PrivateConfigurationFileName),
                privateConfiguration,
                openttdRoot);
            DeleteIfExists(
                ResolveWithinRoot(openttdRoot, ArenaRuntimeLayout.SecretsConfigurationFileName),
                openttdRoot);
            WriteTextIfChanged(
                ResolveWithinRoot(openttdRoot, ArenaRuntimeLayout.ServerConfigurationFileName),
                serverConfiguration,
                openttdRoot);
            WriteTextIfChanged(
                ResolveWithinRoot(openttdRoot, ArenaRuntimeLayout.SpectatorConfigurationFileName),
                spectatorConfiguration,
                openttdRoot);
            createdOrUpdated.Add("generated OpenTTD configuration");

            RuntimeContentManifest manifest = new(
                1,
                [
                    new RuntimePackageManifestEntry(
                        $"{ArenaRuntimeLayout.GameDirectoryName}/{ArenaRuntimeLayout.ArenaGameScriptName}",
                        ComputeDirectorySha256(gameTarget, cancellationToken)),
                    new RuntimePackageManifestEntry(
                        $"{ArenaRuntimeLayout.AiDirectoryName}/{ArenaRuntimeLayout.ModelProxyAiName}",
                        ComputeDirectorySha256(aiTarget, cancellationToken)),
                ]);
            string manifestText = JsonSerializer.Serialize(manifest, ManifestJsonOptions) + Environment.NewLine;
            WriteTextIfChanged(
                ResolveWithinRoot(openttdRoot, ArenaRuntimeLayout.ContentManifestFileName),
                manifestText,
                openttdRoot);
            createdOrUpdated.Add(ArenaRuntimeLayout.ContentManifestFileName);

            return new RuntimeLayoutResult(true, createdOrUpdated, warnings, null);
        }
        catch (IOException exception)
        {
            return Failure("The isolated runtime could not be written. Close any program using .runtime and verify repository write permissions.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure("The isolated runtime is not writable. Grant the repository user write access and rerun bootstrap.", exception);
        }
        catch (ArgumentException exception)
        {
            return Failure("A runtime path is invalid. Restore the example configuration and use repository-relative paths.", exception);
        }
    }

    private static RuntimeLayoutResult Failure(string userMessage, Exception? exception = null) =>
        new(
            false,
            [],
            [],
            new ArenaError(
                ArenaErrorCodes.RuntimeLayoutInvalid,
                userMessage,
                exception?.GetType().Name ?? "runtime-layout-input-invalid",
                false));

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

    private static string ResolveWithinRoot(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Runtime paths must be relative.", nameof(relativePath));
        }

        string candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsWithinRoot(root, candidate))
        {
            throw new ArgumentException("Runtime path escapes its root.", nameof(relativePath));
        }

        return candidate;
    }

    private static void CopyOpenTtdInstallation(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        foreach (string sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(sourceRoot, sourceFile);
            string relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            if (IsMutableOpenTtdFile(relativePath))
            {
                continue;
            }

            string destinationFile = ResolveWithinRoot(destinationRoot, relativePath);
            EnsureNoReparsePoints(destinationRoot, destinationFile);
            string? destinationDirectory = Path.GetDirectoryName(destinationFile);
            if (string.IsNullOrWhiteSpace(destinationDirectory))
            {
                throw new InvalidOperationException("Could not determine the runtime destination directory.");
            }

            Directory.CreateDirectory(destinationDirectory);
            File.Copy(sourceFile, destinationFile, true);
        }
    }

    private static bool IsMutableOpenTtdFile(string relativePath)
    {
        string[] segments = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => UserMutableOpenTtdDirectories.Contains(segment)) ||
            UserMutableOpenTtdFiles.Contains(Path.GetFileName(relativePath));
    }

    private static void ReplaceDirectory(
        string sourceDirectory,
        string destinationDirectory,
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNoReparsePoints(runtimeRoot, destinationDirectory);
        if (Directory.Exists(destinationDirectory))
        {
            Directory.Delete(destinationDirectory, true);
        }

        CopyDirectory(sourceDirectory, destinationDirectory, runtimeRoot, cancellationToken);
    }

    private static void CopyDirectory(
        string sourceDirectory,
        string destinationDirectory,
        string runtimeRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(sourceDirectory, sourceFile);
            string relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            string destinationFile = ResolveWithinRoot(destinationDirectory, relativePath);
            EnsureNoReparsePoints(runtimeRoot, destinationFile);
            string? destinationParent = Path.GetDirectoryName(destinationFile);
            if (string.IsNullOrWhiteSpace(destinationParent))
            {
                throw new InvalidOperationException("Could not determine the package destination directory.");
            }

            Directory.CreateDirectory(destinationParent);
            File.Copy(sourceFile, destinationFile, true);
        }
    }

    private static string RenderOpenTtdConfiguration(int adminPort) =>
        $"""
        # Generated by OpenTTD Model Arena Phase 01. This file is repository-local.
        [version]
        ini_version = 7

        [misc]
        language = english_US.lng
        resolution = 2560,1440
        gui_scale = 100

        [gui]
        autosave_interval = 10
        keep_all_autosave = false
        max_num_autosaves = 16

        [network]
        server_port = {ArenaRuntimeLayout.GameServerPort}
        server_admin_port = {adminPort}
        server_game_type = local
        pause_on_join = true
        # Phase 03 supplies AdminPort authentication from a Credential Manager reference at launch.
        """ + Environment.NewLine;

    private static string RenderPrivateConfiguration(string bindAddress) =>
        $"""
        # Generated by OpenTTD Model Arena Phase 01. This file contains no credentials.
        # OpenTTD stores server bind addresses in private.cfg, not openttd.cfg.
        [private]

        [server_bind_addresses]
        {bindAddress} =
        """ + Environment.NewLine;

    private static string RenderServerConfiguration(int adminPort) =>
        $"""
        # Generated server defaults. Phase 02 owns launching OpenTTD with these values.
        # The loopback bind address is stored in private.cfg.
        set network.server_port {ArenaRuntimeLayout.GameServerPort}
        set network.server_admin_port {adminPort}
        set network.server_game_type local
        set network.pause_on_join true
        """ + Environment.NewLine;

    private static string RenderSpectatorConfiguration() =>
        """
        # Generated spectator defaults. Phase 02 owns client launch arguments.
        # The loopback endpoint is selected from arena.local.yaml at launch time.
        """ + Environment.NewLine;

    private static void WriteTextIfChanged(string path, string content, string runtimeRoot)
    {
        EnsureNoReparsePoints(runtimeRoot, path);
        if (File.Exists(path) &&
            new FileInfo(path).Length == Utf8WithoutBom.GetByteCount(content) &&
            string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(path, content, Utf8WithoutBom);
    }

    private static void DeleteIfExists(string path, string runtimeRoot)
    {
        EnsureNoReparsePoints(runtimeRoot, path);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void EnsureNoReparsePoints(string root, string candidate)
    {
        if (!IsWithinRoot(root, candidate))
        {
            throw new ArgumentException("Runtime path escapes its root.", nameof(candidate));
        }

        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The isolated runtime must not traverse a symbolic link or junction.");
        }

        string relativePath = Path.GetRelativePath(root, candidate);
        string currentPath = Path.GetFullPath(root);
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
                throw new IOException("The isolated runtime must not traverse a symbolic link or junction.");
            }
        }
    }

    internal static string ComputeDirectorySha256(string directory, CancellationToken cancellationToken)
    {
        EnsureNoReparsePoints(directory, directory);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(directory, file);
            string normalizedRelativePath = Path.GetRelativePath(directory, file)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            hash.AppendData(Utf8WithoutBom.GetBytes(normalizedRelativePath));
            hash.AppendData([0]);

            using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, bytesRead);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed record RuntimeContentManifest(
        int ManifestVersion,
        IReadOnlyList<RuntimePackageManifestEntry> Packages);

    private sealed record RuntimePackageManifestEntry(string Path, string Sha256);
}

public sealed record RuntimeLayoutInspection(
    bool IsValid,
    IReadOnlyList<string> MissingOrInvalidItems);

public static class RuntimeLayoutInspector
{
    private const long MaximumInspectableFileBytes = 64 * 1024;
    private static readonly char[] LineSeparators = ['\r', '\n'];

    public static RuntimeLayoutInspection Inspect(
        string runtimeRoot,
        string expectedBindAddress,
        int expectedAdminPort)
    {
        List<string> missingOrInvalid = [];
        if (!IPAddress.TryParse(expectedBindAddress, out IPAddress? bindAddress) ||
            !IPAddress.IsLoopback(bindAddress) ||
            expectedAdminPort is < 1024 or > 65535 ||
            expectedAdminPort == ArenaRuntimeLayout.GameServerPort)
        {
            return new RuntimeLayoutInspection(false, ["runtime inspection input"]);
        }

        string openttdRoot = Path.Combine(runtimeRoot, ArenaRuntimeLayout.OpenTtdDirectoryName);
        InspectPackage(
            openttdRoot,
            ArenaRuntimeLayout.GameDirectoryName,
            ArenaRuntimeLayout.ArenaGameScriptName,
            "RegisterGS",
            missingOrInvalid);
        InspectPackage(
            openttdRoot,
            ArenaRuntimeLayout.AiDirectoryName,
            ArenaRuntimeLayout.ModelProxyAiName,
            "RegisterAI",
            missingOrInvalid);

        InspectContentManifest(openttdRoot, missingOrInvalid);
        InspectGeneratedConfiguration(openttdRoot, expectedBindAddress, expectedAdminPort, missingOrInvalid);

        return new RuntimeLayoutInspection(missingOrInvalid.Count == 0, missingOrInvalid);
    }

    private static void InspectContentManifest(string openttdRoot, List<string> missingOrInvalid)
    {
        string manifestPath = Path.Combine(openttdRoot, ArenaRuntimeLayout.ContentManifestFileName);
        try
        {
            using JsonDocument document = JsonDocument.Parse(ReadSmallFile(manifestPath));
            if (!document.RootElement.TryGetProperty("manifest_version", out JsonElement version) ||
                !version.TryGetInt32(out int manifestVersion) ||
                manifestVersion != 1 ||
                !document.RootElement.TryGetProperty("packages", out JsonElement packages) ||
                packages.ValueKind != JsonValueKind.Array)
            {
                missingOrInvalid.Add("content manifest");
                return;
            }

            Dictionary<string, string> hashes = new(StringComparer.Ordinal);
            foreach (JsonElement package in packages.EnumerateArray())
            {
                if (!package.TryGetProperty("path", out JsonElement path) ||
                    path.ValueKind != JsonValueKind.String ||
                    !package.TryGetProperty("sha256", out JsonElement hash) ||
                    hash.ValueKind != JsonValueKind.String)
                {
                    missingOrInvalid.Add("content manifest");
                    return;
                }

                string? packagePath = path.GetString();
                string? packageHash = hash.GetString();
                if (string.IsNullOrWhiteSpace(packagePath) ||
                    string.IsNullOrWhiteSpace(packageHash) ||
                    !hashes.TryAdd(packagePath, packageHash))
                {
                    missingOrInvalid.Add("content manifest");
                    return;
                }
            }

            foreach (string requiredPackage in new[]
            {
                $"{ArenaRuntimeLayout.GameDirectoryName}/{ArenaRuntimeLayout.ArenaGameScriptName}",
                $"{ArenaRuntimeLayout.AiDirectoryName}/{ArenaRuntimeLayout.ModelProxyAiName}",
            })
            {
                if (!hashes.TryGetValue(requiredPackage, out string? hash) ||
                    !IsSha256(hash) ||
                    !string.Equals(
                        hash,
                        RuntimeLayoutBuilder.ComputeDirectorySha256(
                            Path.Combine(openttdRoot, requiredPackage),
                            CancellationToken.None),
                        StringComparison.Ordinal))
                {
                    missingOrInvalid.Add("content manifest");
                    return;
                }
            }
        }
        catch (IOException)
        {
            missingOrInvalid.Add("content manifest");
        }
        catch (JsonException)
        {
            missingOrInvalid.Add("content manifest");
        }
    }

    private static void InspectGeneratedConfiguration(
        string openttdRoot,
        string expectedBindAddress,
        int expectedAdminPort,
        List<string> missingOrInvalid)
    {
        try
        {
            Dictionary<string, Dictionary<string, string>> openTtdConfiguration = ParseIni(
                Path.Combine(openttdRoot, ArenaRuntimeLayout.OpenTtdConfigurationFileName));
            RequireIniValue(openTtdConfiguration, "version", "ini_version", "7", "openttd.cfg", missingOrInvalid);
            RequireIniValue(openTtdConfiguration, "misc", "language", "english_US.lng", "openttd.cfg", missingOrInvalid);
            RequireIniValue(openTtdConfiguration, "misc", "resolution", "2560,1440", "openttd.cfg", missingOrInvalid);
            RequireIniValue(openTtdConfiguration, "misc", "gui_scale", "100", "openttd.cfg", missingOrInvalid);
            RequireIniValue(openTtdConfiguration, "gui", "autosave_interval", "10", "openttd.cfg", missingOrInvalid);
            RequireIniValue(openTtdConfiguration, "gui", "keep_all_autosave", "false", "openttd.cfg", missingOrInvalid);
            RequireIniValue(openTtdConfiguration, "gui", "max_num_autosaves", "16", "openttd.cfg", missingOrInvalid);
            RequireIniValue(openTtdConfiguration, "network", "server_port", ArenaRuntimeLayout.GameServerPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "openttd.cfg", missingOrInvalid);
            RequireIniValue(openTtdConfiguration, "network", "server_admin_port", expectedAdminPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "openttd.cfg", missingOrInvalid);
            RequireIniValue(openTtdConfiguration, "network", "server_game_type", "local", "openttd.cfg", missingOrInvalid);
            RequireIniValue(openTtdConfiguration, "network", "pause_on_join", "true", "openttd.cfg", missingOrInvalid);

            Dictionary<string, Dictionary<string, string>> privateConfiguration = ParseIni(
                Path.Combine(openttdRoot, ArenaRuntimeLayout.PrivateConfigurationFileName));
            RequireIniValue(privateConfiguration, "server_bind_addresses", expectedBindAddress, string.Empty, "private.cfg", missingOrInvalid);

            Dictionary<string, string> serverCommands = ReadServerCommands(
                Path.Combine(openttdRoot, ArenaRuntimeLayout.ServerConfigurationFileName));
            KeyValuePair<string, string>[] requiredCommands =
            {
                new("network.server_port", ArenaRuntimeLayout.GameServerPort.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("network.server_admin_port", expectedAdminPort.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("network.server_game_type", "local"),
                new("network.pause_on_join", "true"),
            };
            if (serverCommands.Count != requiredCommands.Length)
            {
                missingOrInvalid.Add("server.cfg");
            }
            else
            {
                foreach ((string key, string value) in requiredCommands)
                {
                    if (!serverCommands.TryGetValue(key, out string? actualValue) ||
                        !string.Equals(actualValue, value, StringComparison.Ordinal))
                    {
                        missingOrInvalid.Add("server.cfg");
                        break;
                    }
                }
            }

            string spectatorConfiguration = ReadSmallFile(
                Path.Combine(openttdRoot, ArenaRuntimeLayout.SpectatorConfigurationFileName));
            if (!spectatorConfiguration.StartsWith("# Generated spectator defaults.", StringComparison.Ordinal))
            {
                missingOrInvalid.Add("spectator.cfg");
            }
        }
        catch (IOException)
        {
            missingOrInvalid.Add("generated OpenTTD configuration");
        }
        catch (InvalidDataException)
        {
            missingOrInvalid.Add("generated OpenTTD configuration");
        }
    }

    private static Dictionary<string, Dictionary<string, string>> ParseIni(string path)
    {
        Dictionary<string, Dictionary<string, string>> sections = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? currentSection = null;
        foreach (string rawLine in ReadSmallFile(path).Split(LineSeparators, StringSplitOptions.None))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                string sectionName = line[1..^1].Trim();
                if (sectionName.Length == 0 || !sections.TryAdd(sectionName, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException("The generated INI configuration has an invalid section.");
                }

                currentSection = sections[sectionName];
                continue;
            }

            int separator = line.IndexOf('=');
            if (currentSection is null || separator < 1)
            {
                throw new InvalidDataException("The generated INI configuration has an invalid value.");
            }

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (key.Length == 0 || !currentSection.TryAdd(key, value))
            {
                throw new InvalidDataException("The generated INI configuration has duplicate or empty keys.");
            }
        }

        return sections;
    }

    private static void RequireIniValue(
        Dictionary<string, Dictionary<string, string>> sections,
        string section,
        string key,
        string expectedValue,
        string configurationFile,
        List<string> missingOrInvalid)
    {
        if (!sections.TryGetValue(section, out Dictionary<string, string>? values) ||
            !values.TryGetValue(key, out string? value) ||
            !string.Equals(value, expectedValue, StringComparison.Ordinal))
        {
            missingOrInvalid.Add(configurationFile);
        }
    }

    private static Dictionary<string, string> ReadServerCommands(string path)
    {
        Dictionary<string, string> commands = new(StringComparer.Ordinal);
        foreach (string line in ReadSmallFile(path)
                     .Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(line => !line.StartsWith('#')))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 3 || !string.Equals(parts[0], "set", StringComparison.Ordinal) ||
                !commands.TryAdd(parts[1], parts[2]))
            {
                throw new InvalidDataException("The generated server configuration is invalid.");
            }
        }

        return commands;
    }

    private static string ReadSmallFile(string path)
    {
        FileInfo file = new(path);
        if (!file.Exists || file.Length > MaximumInspectableFileBytes)
        {
            throw new IOException("The runtime file is missing or exceeds the inspection limit.");
        }

        return File.ReadAllText(path);
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            (character is >= '0' and <= '9') ||
            (character is >= 'a' and <= 'f'));

    private static void InspectPackage(
        string openttdRoot,
        string packageKind,
        string packageName,
        string registrationFunction,
        List<string> missingOrInvalid)
    {
        string packageRoot = Path.Combine(openttdRoot, packageKind, packageName);
        string infoPath = Path.Combine(packageRoot, "info.nut");
        string mainPath = Path.Combine(packageRoot, "main.nut");
        if (!File.Exists(infoPath) || !File.Exists(mainPath))
        {
            missingOrInvalid.Add($"{packageName} package");
            return;
        }

        string metadata = ReadSmallFile(infoPath);
        if (!metadata.Contains(packageName, StringComparison.Ordinal) ||
            !metadata.Contains(registrationFunction, StringComparison.Ordinal))
        {
            missingOrInvalid.Add($"{packageName} metadata");
        }
    }
}
