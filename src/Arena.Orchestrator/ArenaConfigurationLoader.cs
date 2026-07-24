using System.Globalization;
using System.Text.RegularExpressions;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Storage;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace OpenTtd.ModelArena.Orchestrator;

public sealed record ArenaLocalConfiguration(
    string RepositoryRoot,
    string ConfigurationPath,
    RuntimeLocalConfiguration Runtime,
    OpenTtdLocalConfiguration OpenTtd,
    ObsLocalConfiguration Obs,
    NetworkLocalConfiguration Network,
    LoggingLocalConfiguration Logging,
    DoctorLocalConfiguration Doctor);

public sealed record RuntimeLocalConfiguration(
    string Root,
    string Runs,
    string Recordings);

public sealed record OpenTtdLocalConfiguration(
    string Executable,
    string ServerConfiguration,
    string SpectatorConfiguration,
    int AdminPort,
    CredentialReference AdminCredentialReference);

public sealed record ObsLocalConfiguration(
    string Host,
    int Port,
    CredentialReference CredentialReference,
    string SceneCollection,
    string Executable);

public sealed record NetworkLocalConfiguration(string BindAddress);

public sealed record LoggingLocalConfiguration(string Level, bool RedactSecrets);

public sealed record DoctorLocalConfiguration(int MinimumFreeDiskGigabytes);

public sealed record ProviderLocalConfiguration(
    string Id,
    string Type,
    Uri? BaseUri,
    CredentialReference? CredentialReference);

public sealed record ProviderLocalConfigurationSet(
    string ConfigurationPath,
    IReadOnlyDictionary<string, ProviderLocalConfiguration> Providers);

public sealed record ConfigurationValidationError(
    string Field,
    string Code,
    string Message);

public sealed record ArenaConfigurationLoadResult(
    ArenaLocalConfiguration? Configuration,
    IReadOnlyList<ConfigurationValidationError> Errors)
{
    public bool Succeeded => Configuration is not null && Errors.Count == 0;
}

public sealed record ProviderConfigurationLoadResult(
    ProviderLocalConfigurationSet? Configuration,
    IReadOnlyList<ConfigurationValidationError> Errors)
{
    public bool Succeeded => Configuration is not null && Errors.Count == 0;
}

/// <summary>
/// Parses the repository's deliberately small local YAML surface. Unknown
/// fields are errors, so raw secrets cannot silently become configuration.
/// </summary>
public static class ArenaConfigurationLoader
{
    private const long MaximumConfigurationBytes = 256 * 1024;
    private const int MaximumStringLength = 2048;
    private const int MaximumRepositoryPathLength = 260;
    private const int MaximumSceneCollectionLength = 120;
    private const int MaximumProviderTypeLength = 64;
    private const int MaximumMappingEntries = 100;
    private const int MaximumYamlDepth = 16;
    private const int MaximumYamlNodes = 512;

    public static Task<ArenaConfigurationLoadResult> LoadArenaAsync(
        string repositoryRoot,
        string configurationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(LoadArena(repositoryRoot, configurationPath));
    }

    public static Task<ProviderConfigurationLoadResult> LoadProvidersAsync(
        string repositoryRoot,
        string configurationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(LoadProviders(repositoryRoot, configurationPath));
    }

    private static ArenaConfigurationLoadResult LoadArena(string repositoryRoot, string configurationPath)
    {
        List<ConfigurationValidationError> errors = [];
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
        {
            AddError(errors, "repository_root", ArenaErrorCodes.ConfigurationInvalid, "The repository root does not exist.");
            return new ArenaConfigurationLoadResult(null, errors);
        }

        string normalizedRepositoryRoot = Path.GetFullPath(repositoryRoot);
        string? validatedConfigurationPath = ResolveConfigurationFilePath(
            normalizedRepositoryRoot,
            configurationPath,
            errors);
        if (validatedConfigurationPath is null)
        {
            return new ArenaConfigurationLoadResult(null, errors);
        }

        YamlMappingNode? root = LoadRootMapping(validatedConfigurationPath, errors);
        if (root is null)
        {
            return new ArenaConfigurationLoadResult(null, errors);
        }

        Dictionary<string, YamlNode> rootValues = ReadMapping(root, "root", errors);
        ValidateKnownFields(
            rootValues,
            ["config_version", "runtime", "openttd", "obs", "network", "logging", "doctor"],
            "root",
            errors);
        ValidateVersion(rootValues, "config_version", errors);

        Dictionary<string, YamlNode> runtimeValues = ReadRequiredMapping(rootValues, "runtime", "runtime", errors);
        Dictionary<string, YamlNode> openttdValues = ReadRequiredMapping(rootValues, "openttd", "openttd", errors);
        Dictionary<string, YamlNode> obsValues = ReadRequiredMapping(rootValues, "obs", "obs", errors);
        Dictionary<string, YamlNode> networkValues = ReadRequiredMapping(rootValues, "network", "network", errors);
        Dictionary<string, YamlNode> loggingValues = ReadRequiredMapping(rootValues, "logging", "logging", errors);
        Dictionary<string, YamlNode> doctorValues = ReadRequiredMapping(rootValues, "doctor", "doctor", errors);

        ValidateKnownFields(runtimeValues, ["root", "runs", "recordings"], "runtime", errors);
        ValidateKnownFields(openttdValues, ["executable", "server_config", "spectator_config", "admin_port", "admin_credential_ref"], "openttd", errors);
        ValidateKnownFields(obsValues, ["host", "port", "credential_ref", "scene_collection", "executable"], "obs", errors);
        ValidateKnownFields(networkValues, ["bind_address"], "network", errors);
        ValidateKnownFields(loggingValues, ["level", "redact_secrets"], "logging", errors);
        ValidateKnownFields(doctorValues, ["minimum_free_disk_gb"], "doctor", errors);

        string? runtimeRoot = ResolveRepositoryPath(
            normalizedRepositoryRoot,
            RequiredString(runtimeValues, "root", "runtime.root", errors),
            "runtime.root",
            errors);
        string? runsRoot = ResolveRepositoryPath(
            normalizedRepositoryRoot,
            RequiredString(runtimeValues, "runs", "runtime.runs", errors),
            "runtime.runs",
            errors);
        string? recordingsRoot = ResolveRepositoryPath(
            normalizedRepositoryRoot,
            RequiredString(runtimeValues, "recordings", "runtime.recordings", errors),
            "runtime.recordings",
            errors);
        string? openttdExecutable = ResolveRepositoryPath(
            normalizedRepositoryRoot,
            RequiredString(openttdValues, "executable", "openttd.executable", errors),
            "openttd.executable",
            errors);
        string? serverConfiguration = ResolveRepositoryPath(
            normalizedRepositoryRoot,
            RequiredString(openttdValues, "server_config", "openttd.server_config", errors),
            "openttd.server_config",
            errors);
        string? spectatorConfiguration = ResolveRepositoryPath(
            normalizedRepositoryRoot,
            RequiredString(openttdValues, "spectator_config", "openttd.spectator_config", errors),
            "openttd.spectator_config",
            errors);

        int? adminPort = RequiredInteger(openttdValues, "admin_port", "openttd.admin_port", 1024, 65535, errors);
        string? adminCredentialText = RequiredString(openttdValues, "admin_credential_ref", "openttd.admin_credential_ref", errors);
        string? obsHost = RequiredString(obsValues, "host", "obs.host", errors);
        int? obsPort = RequiredInteger(obsValues, "port", "obs.port", 1, 65535, errors);
        string? obsCredentialText = RequiredString(obsValues, "credential_ref", "obs.credential_ref", errors);
        string? sceneCollection = RequiredString(obsValues, "scene_collection", "obs.scene_collection", errors);
        string obsExecutable = OptionalString(obsValues, "executable", "obs.executable", errors) ?? "obs64";
        string? bindAddress = RequiredString(networkValues, "bind_address", "network.bind_address", errors);
        string? logLevel = RequiredString(loggingValues, "level", "logging.level", errors);
        bool? redactSecrets = RequiredBoolean(loggingValues, "redact_secrets", "logging.redact_secrets", errors);
        int? minimumFreeDisk = RequiredInteger(
            doctorValues,
            "minimum_free_disk_gb",
            "doctor.minimum_free_disk_gb",
            1,
            10_000,
            errors);

        if (runtimeRoot is not null && runsRoot is not null && !IsWithinRoot(runtimeRoot, runsRoot))
        {
            AddError(errors, "runtime.runs", ArenaErrorCodes.ConfigurationInvalid, "runtime.runs must be below runtime.root.");
        }

        if (runtimeRoot is not null && recordingsRoot is not null && !IsWithinRoot(runtimeRoot, recordingsRoot))
        {
            AddError(errors, "runtime.recordings", ArenaErrorCodes.ConfigurationInvalid, "runtime.recordings must be below runtime.root.");
        }

        if (runtimeRoot is not null)
        {
            foreach ((string field, string? value) in new[]
            {
                ("openttd.executable", openttdExecutable),
                ("openttd.server_config", serverConfiguration),
                ("openttd.spectator_config", spectatorConfiguration),
            })
            {
                if (value is not null && !IsWithinRoot(runtimeRoot, value))
                {
                    AddError(errors, field, ArenaErrorCodes.ConfigurationInvalid, $"{field} must be below runtime.root.");
                }
            }
        }

        if (!IsLoopbackAddress(obsHost))
        {
            AddError(errors, "obs.host", ArenaErrorCodes.ConfigurationInvalid, "obs.host must be a loopback address.");
        }

        if (!IsLoopbackAddress(bindAddress))
        {
            AddError(errors, "network.bind_address", ArenaErrorCodes.ConfigurationInvalid, "network.bind_address must be a loopback address.");
        }

        if (!IsKnownLogLevel(logLevel))
        {
            AddError(errors, "logging.level", ArenaErrorCodes.ConfigurationInvalid, "logging.level must be Trace, Debug, Information, Warning, Error, or Critical.");
        }

        if (redactSecrets is false)
        {
            AddError(errors, "logging.redact_secrets", ArenaErrorCodes.ConfigurationInvalid, "logging.redact_secrets must be true for all Arena diagnostics.");
        }

        CredentialReference? obsCredential = ParseCredentialReference(obsCredentialText, "obs.credential_ref", errors);
        CredentialReference? adminCredential = ParseCredentialReference(adminCredentialText, "openttd.admin_credential_ref", errors);
        if (adminPort == ArenaRuntimeLayout.GameServerPort)
        {
            AddError(
                errors,
                "openttd.admin_port",
                ArenaErrorCodes.ConfigurationInvalid,
                $"openttd.admin_port must not equal the generated game server port {ArenaRuntimeLayout.GameServerPort}.");
        }

        if (sceneCollection is { Length: > MaximumSceneCollectionLength })
        {
            AddError(
                errors,
                "obs.scene_collection",
                ArenaErrorCodes.ConfigurationInvalid,
                $"obs.scene_collection must be no longer than {MaximumSceneCollectionLength} characters.");
        }

        if (adminCredential is not null &&
            obsCredential is not null &&
            string.Equals(adminCredential.Target, obsCredential.Target, StringComparison.OrdinalIgnoreCase))
        {
            AddError(
                errors,
                "openttd.admin_credential_ref",
                ArenaErrorCodes.ConfigurationInvalid,
                "openttd.admin_credential_ref must be a dedicated credential and must not reuse the OBS credential reference.");
        }

        ValidateExecutableName(obsExecutable, "obs.executable", errors);
        if (errors.Count > 0 ||
            runtimeRoot is null ||
            runsRoot is null ||
            recordingsRoot is null ||
            openttdExecutable is null ||
            serverConfiguration is null ||
            spectatorConfiguration is null ||
            adminPort is null ||
            adminCredential is null ||
            obsHost is null ||
            obsPort is null ||
            obsCredential is null ||
            sceneCollection is null ||
            bindAddress is null ||
            logLevel is null ||
            redactSecrets is null ||
            minimumFreeDisk is null)
        {
            return new ArenaConfigurationLoadResult(null, errors);
        }

        return new ArenaConfigurationLoadResult(
            new ArenaLocalConfiguration(
                normalizedRepositoryRoot,
                validatedConfigurationPath,
                new RuntimeLocalConfiguration(runtimeRoot, runsRoot, recordingsRoot),
                new OpenTtdLocalConfiguration(openttdExecutable, serverConfiguration, spectatorConfiguration, adminPort.Value, adminCredential),
                new ObsLocalConfiguration(obsHost, obsPort.Value, obsCredential, sceneCollection, obsExecutable),
                new NetworkLocalConfiguration(bindAddress),
                new LoggingLocalConfiguration(logLevel, redactSecrets.Value),
                new DoctorLocalConfiguration(minimumFreeDisk.Value)),
            errors);
    }

    private static ProviderConfigurationLoadResult LoadProviders(string repositoryRoot, string configurationPath)
    {
        List<ConfigurationValidationError> errors = [];
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
        {
            AddError(errors, "repository_root", ArenaErrorCodes.ConfigurationInvalid, "The repository root does not exist.");
            return new ProviderConfigurationLoadResult(null, errors);
        }

        string normalizedRepositoryRoot = Path.GetFullPath(repositoryRoot);
        string? validatedConfigurationPath = ResolveConfigurationFilePath(
            normalizedRepositoryRoot,
            configurationPath,
            errors);
        if (validatedConfigurationPath is null)
        {
            return new ProviderConfigurationLoadResult(null, errors);
        }

        YamlMappingNode? root = LoadRootMapping(validatedConfigurationPath, errors);
        if (root is null)
        {
            return new ProviderConfigurationLoadResult(null, errors);
        }

        Dictionary<string, YamlNode> rootValues = ReadMapping(root, "root", errors);
        ValidateKnownFields(rootValues, ["config_version", "providers"], "root", errors);
        ValidateVersion(rootValues, "config_version", errors);
        Dictionary<string, YamlNode> providerNodes = ReadRequiredMapping(rootValues, "providers", "providers", errors);
        if (providerNodes.Count > 100)
        {
            AddError(errors, "providers", ArenaErrorCodes.ConfigurationInvalid, "providers contains more than the 100-entry safety limit.");
        }

        Dictionary<string, ProviderLocalConfiguration> providers = new(StringComparer.Ordinal);
        foreach ((string providerId, YamlNode providerNode) in providerNodes)
        {
            string field = $"providers.{providerId}";
            if (!IsValidProviderId(providerId))
            {
                AddError(errors, field, ArenaErrorCodes.ConfigurationInvalid, "Provider identifiers must use lowercase letters, digits, hyphens, or underscores.");
                continue;
            }

            Dictionary<string, YamlNode> providerValues = ReadMapping(providerNode, field, errors);
            ValidateKnownFields(providerValues, ["type", "base_url", "credential_ref"], field, errors);
            string? type = RequiredString(providerValues, "type", $"{field}.type", errors);
            string? baseUrlText = OptionalString(providerValues, "base_url", $"{field}.base_url", errors);
            string? credentialText = OptionalString(providerValues, "credential_ref", $"{field}.credential_ref", errors);
            Uri? baseUri = null;
            if (type is { Length: > MaximumProviderTypeLength })
            {
                AddError(
                    errors,
                    $"{field}.type",
                    ArenaErrorCodes.ConfigurationInvalid,
                    $"{field}.type must be no longer than {MaximumProviderTypeLength} characters.");
            }

            if (baseUrlText is not null &&
                (!Uri.TryCreate(baseUrlText, UriKind.Absolute, out baseUri) ||
                 baseUri.Scheme != Uri.UriSchemeHttps ||
                 !string.IsNullOrEmpty(baseUri.UserInfo) ||
                 !string.IsNullOrEmpty(baseUri.Query) ||
                 !string.IsNullOrEmpty(baseUri.Fragment)))
            {
                AddError(errors, $"{field}.base_url", ArenaErrorCodes.ConfigurationInvalid, "Provider base_url must be an absolute HTTPS URL without user info, query, or fragment.");
            }

            CredentialReference? credentialReference = credentialText is null
                ? null
                : ParseCredentialReference(credentialText, $"{field}.credential_ref", errors);
            if (type is not null)
            {
                providers[providerId] = new ProviderLocalConfiguration(providerId, type, baseUri, credentialReference);
            }
        }

        if (errors.Count > 0)
        {
            return new ProviderConfigurationLoadResult(null, errors);
        }

        return new ProviderConfigurationLoadResult(
            new ProviderLocalConfigurationSet(validatedConfigurationPath, providers),
            errors);
    }

    private static YamlMappingNode? LoadRootMapping(
        string configurationPath,
        List<ConfigurationValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(configurationPath) || !File.Exists(configurationPath))
        {
            AddError(errors, "file", ArenaErrorCodes.ConfigurationInvalid, "The local configuration file is missing. Run bootstrap to create it from the repository example.");
            return null;
        }

        try
        {
            FileInfo fileInfo = new(configurationPath);
            if (fileInfo.Length > MaximumConfigurationBytes)
            {
                AddError(errors, "file", ArenaErrorCodes.ConfigurationInvalid, "The local configuration file exceeds the 256 KiB safety limit.");
                return null;
            }

            string yamlText = File.ReadAllText(configurationPath);
            if (ContainsRawSecretField(yamlText))
            {
                AddError(errors, "file", ArenaErrorCodes.ConfigurationSecretDetected, "Local configuration may contain credential references only; move any raw secret to Windows Credential Manager.");
            }

            YamlStream stream = new();
            using StringReader reader = new(yamlText);
            stream.Load(reader);
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                AddError(errors, "root", ArenaErrorCodes.ConfigurationInvalid, "The local configuration must contain one YAML mapping document.");
                return null;
            }

            if (!ValidateYamlStructure(root, errors))
            {
                return null;
            }

            return root;
        }
        catch (YamlException)
        {
            AddError(errors, "file", ArenaErrorCodes.ConfigurationInvalid, "The local configuration is not valid YAML. Restore the example and edit it again.");
            return null;
        }
        catch (IOException)
        {
            AddError(errors, "file", ArenaErrorCodes.ConfigurationInvalid, "The local configuration could not be read. Verify repository permissions and try again.");
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            AddError(errors, "file", ArenaErrorCodes.ConfigurationInvalid, "The local configuration is not readable. Grant the repository user read access and try again.");
            return null;
        }
    }

    private static Dictionary<string, YamlNode> ReadRequiredMapping(
        Dictionary<string, YamlNode> values,
        string key,
        string field,
        List<ConfigurationValidationError> errors)
    {
        if (!values.TryGetValue(key, out YamlNode? node))
        {
            AddError(errors, field, ArenaErrorCodes.ConfigurationInvalid, $"{field} is required.");
            return new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        }

        return ReadMapping(node, field, errors);
    }

    private static Dictionary<string, YamlNode> ReadMapping(
        YamlNode node,
        string field,
        List<ConfigurationValidationError> errors)
    {
        if (node is not YamlMappingNode mapping)
        {
            AddError(errors, field, ArenaErrorCodes.ConfigurationInvalid, $"{field} must be a mapping.");
            return new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        }

        Dictionary<string, YamlNode> values = new(StringComparer.Ordinal);
        foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
        {
            if (values.Count >= MaximumMappingEntries)
            {
                AddError(errors, field, ArenaErrorCodes.ConfigurationInvalid, $"{field} contains more than the {MaximumMappingEntries}-entry safety limit.");
                break;
            }

            if (pair.Key is not YamlScalarNode { Value: { } key } || key.Length == 0)
            {
                AddError(errors, field, ArenaErrorCodes.ConfigurationInvalid, $"{field} contains an invalid key.");
                continue;
            }

            if (!values.TryAdd(key, pair.Value))
            {
                AddError(errors, $"{field}.{key}", ArenaErrorCodes.ConfigurationInvalid, "Duplicate configuration keys are not allowed.");
            }
        }

        return values;
    }

    private static void ValidateKnownFields(
        Dictionary<string, YamlNode> values,
        IReadOnlyCollection<string> allowedFields,
        string field,
        List<ConfigurationValidationError> errors)
    {
        foreach (string key in values.Keys)
        {
            if (!allowedFields.Contains(key, StringComparer.Ordinal))
            {
                string code = LooksLikeSecretField(key)
                    ? ArenaErrorCodes.ConfigurationSecretDetected
                    : ArenaErrorCodes.ConfigurationInvalid;
                AddError(errors, $"{field}.{key}", code, $"{field}.{key} is not a supported configuration field.");
            }
        }
    }

    private static void ValidateVersion(
        Dictionary<string, YamlNode> values,
        string key,
        List<ConfigurationValidationError> errors)
    {
        int? value = RequiredInteger(values, key, key, 1, 1, errors);
        if (value is null)
        {
            return;
        }
    }

    private static string? RequiredString(
        Dictionary<string, YamlNode> values,
        string key,
        string field,
        List<ConfigurationValidationError> errors)
    {
        if (!values.TryGetValue(key, out YamlNode? node))
        {
            AddError(errors, field, ArenaErrorCodes.ConfigurationInvalid, $"{field} is required.");
            return null;
        }

        return ReadString(node, field, errors);
    }

    private static string? OptionalString(
        Dictionary<string, YamlNode> values,
        string key,
        string field,
        List<ConfigurationValidationError> errors) =>
        values.TryGetValue(key, out YamlNode? node) ? ReadString(node, field, errors) : null;

    private static string? ReadString(
        YamlNode node,
        string field,
        List<ConfigurationValidationError> errors)
    {
        if (node is not YamlScalarNode { Value: { } value } ||
            string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumStringLength ||
            value.Any(char.IsControl))
        {
            AddError(errors, field, ArenaErrorCodes.ConfigurationInvalid, $"{field} must be a non-empty scalar no longer than {MaximumStringLength} characters.");
            return null;
        }

        return value;
    }

    private static int? RequiredInteger(
        Dictionary<string, YamlNode> values,
        string key,
        string field,
        int minimum,
        int maximum,
        List<ConfigurationValidationError> errors)
    {
        string? text = RequiredString(values, key, field, errors);
        if (text is null ||
            !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ||
            value < minimum ||
            value > maximum)
        {
            AddError(errors, field, ArenaErrorCodes.ConfigurationInvalid, $"{field} must be an integer between {minimum} and {maximum}.");
            return null;
        }

        return value;
    }

    private static bool? RequiredBoolean(
        Dictionary<string, YamlNode> values,
        string key,
        string field,
        List<ConfigurationValidationError> errors)
    {
        string? text = RequiredString(values, key, field, errors);
        if (text is null || !bool.TryParse(text, out bool value))
        {
            AddError(errors, field, ArenaErrorCodes.ConfigurationInvalid, $"{field} must be true or false.");
            return null;
        }

        return value;
    }

    private static string? ResolveRepositoryPath(
        string repositoryRoot,
        string? configuredPath,
        string field,
        List<ConfigurationValidationError> errors)
    {
        if (configuredPath is null)
        {
            return null;
        }

        if (Path.IsPathRooted(configuredPath))
        {
            AddError(errors, field, ArenaErrorCodes.ConfigurationInvalid, $"{field} must be repository-relative.");
            return null;
        }

        if (configuredPath.Length > MaximumRepositoryPathLength)
        {
            AddError(errors, field, ArenaErrorCodes.ConfigurationInvalid, $"{field} must be no longer than {MaximumRepositoryPathLength} characters.");
            return null;
        }

        string resolved = Path.GetFullPath(Path.Combine(repositoryRoot, configuredPath));
        if (!IsWithinRoot(repositoryRoot, resolved))
        {
            AddError(errors, field, ArenaErrorCodes.ConfigurationInvalid, $"{field} must remain below the repository root.");
            return null;
        }

        if (ContainsExistingReparsePoint(repositoryRoot, resolved))
        {
            AddError(errors, field, ArenaErrorCodes.ConfigurationInvalid, $"{field} must not traverse a symbolic link or junction.");
            return null;
        }

        return resolved;
    }

    private static string? ResolveConfigurationFilePath(
        string repositoryRoot,
        string configurationPath,
        List<ConfigurationValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            AddError(errors, "file", ArenaErrorCodes.ConfigurationInvalid, "The local configuration file path is required.");
            return null;
        }

        try
        {
            string resolved = Path.GetFullPath(configurationPath);
            if (!IsWithinRoot(repositoryRoot, resolved))
            {
                AddError(errors, "file", ArenaErrorCodes.ConfigurationInvalid, "The local configuration file must remain below the repository root.");
                return null;
            }

            if (ContainsExistingReparsePoint(repositoryRoot, resolved))
            {
                AddError(errors, "file", ArenaErrorCodes.ConfigurationInvalid, "The local configuration file must not traverse a symbolic link or junction.");
                return null;
            }

            return resolved;
        }
        catch (ArgumentException)
        {
            AddError(errors, "file", ArenaErrorCodes.ConfigurationInvalid, "The local configuration file path is invalid.");
            return null;
        }
        catch (NotSupportedException)
        {
            AddError(errors, "file", ArenaErrorCodes.ConfigurationInvalid, "The local configuration file path is invalid.");
            return null;
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

    private static bool ContainsExistingReparsePoint(string root, string candidate)
    {
        try
        {
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
                    return true;
                }
            }

            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static CredentialReference? ParseCredentialReference(
        string? text,
        string field,
        List<ConfigurationValidationError> errors)
    {
        if (CredentialReference.TryParse(text, out CredentialReference? reference) &&
            reference is not null &&
            reference.IsArenaManaged)
        {
            return reference;
        }

        AddError(errors, field, ArenaErrorCodes.CredentialReferenceInvalid, $"{field} must use an OpenTTDModelArena credman: reference, not a secret value.");
        return null;
    }

    private static bool IsLoopbackAddress(string? address) =>
        string.Equals(address, "127.0.0.1", StringComparison.Ordinal) ||
        string.Equals(address, "::1", StringComparison.Ordinal);

    private static bool IsKnownLogLevel(string? level) =>
        level is "Trace" or "Debug" or "Information" or "Warning" or "Error" or "Critical";

    private static void ValidateExecutableName(
        string executable,
        string field,
        List<ConfigurationValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(executable) ||
            executable.Length > 260 ||
            executable.Any(char.IsControl))
        {
            AddError(errors, field, ArenaErrorCodes.ConfigurationInvalid, "obs.executable must be a simple executable name or configured path.");
        }
    }

    private static bool IsValidProviderId(string providerId) =>
        providerId.Length is > 0 and <= 63 &&
        providerId.All(character =>
            (character is >= 'a' and <= 'z') ||
            (character is >= '0' and <= '9') ||
            character is '-' or '_');

    private static bool ContainsRawSecretField(string yamlText) =>
        Regex.IsMatch(
            yamlText,
            @"(?im)^\s*(?:api[_-]?key|password|secret|token)\s*:",
            RegexOptions.CultureInvariant);

    private static bool ValidateYamlStructure(
        YamlNode root,
        List<ConfigurationValidationError> errors)
    {
        Stack<(YamlNode Node, int Depth)> pending = new();
        pending.Push((root, 0));
        int visitedNodeCount = 0;
        while (pending.Count > 0)
        {
            (YamlNode node, int depth) = pending.Pop();
            visitedNodeCount++;
            if (visitedNodeCount > MaximumYamlNodes)
            {
                AddError(errors, "file", ArenaErrorCodes.ConfigurationInvalid, $"The local configuration exceeds the {MaximumYamlNodes}-node safety limit.");
                return false;
            }

            if (depth > MaximumYamlDepth)
            {
                AddError(errors, "file", ArenaErrorCodes.ConfigurationInvalid, $"The local configuration exceeds the {MaximumYamlDepth}-level nesting safety limit.");
                return false;
            }

            switch (node)
            {
                case YamlScalarNode:
                    break;
                case YamlMappingNode mapping:
                    if (mapping.Children.Count > MaximumMappingEntries)
                    {
                        AddError(errors, "file", ArenaErrorCodes.ConfigurationInvalid, $"The local configuration contains a mapping with more than {MaximumMappingEntries} entries.");
                        return false;
                    }

                    foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
                    {
                        pending.Push((pair.Key, depth + 1));
                        pending.Push((pair.Value, depth + 1));
                    }

                    break;
                case YamlSequenceNode:
                    AddError(errors, "file", ArenaErrorCodes.ConfigurationInvalid, "The local configuration uses a sequence where the closed Phase 01 configuration permits mappings and scalar values only.");
                    return false;
                case YamlNode when node.NodeType == YamlNodeType.Alias:
                    AddError(errors, "file", ArenaErrorCodes.ConfigurationInvalid, "The local configuration must not use YAML aliases.");
                    return false;
                default:
                    AddError(errors, "file", ArenaErrorCodes.ConfigurationInvalid, "The local configuration contains an unsupported YAML node.");
                    return false;
            }
        }

        return true;
    }

    private static bool LooksLikeSecretField(string field) =>
        field.Contains("key", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        field.Contains("token", StringComparison.OrdinalIgnoreCase);

    private static void AddError(
        List<ConfigurationValidationError> errors,
        string field,
        string code,
        string message)
    {
        if (errors.Count < 50)
        {
            errors.Add(new ConfigurationValidationError(field, code, message));
        }
    }
}
