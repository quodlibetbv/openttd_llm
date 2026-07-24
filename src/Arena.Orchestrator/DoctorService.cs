using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Obs;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Orchestrator;

public interface IDoctorClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemDoctorClock : IDoctorClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class DoctorService
{
    private const long BytesPerGigabyte = 1024L * 1024L * 1024L;

    private readonly IDoctorSystemProbe _systemProbe;
    private readonly ICredentialStore _credentialStore;
    private readonly IObsWebSocketInspector _obsWebSocketInspector;
    private readonly IDoctorClock _clock;

    public DoctorService(
        IDoctorSystemProbe systemProbe,
        ICredentialStore credentialStore,
        IObsWebSocketInspector obsWebSocketInspector,
        IDoctorClock clock)
    {
        _systemProbe = systemProbe;
        _credentialStore = credentialStore;
        _obsWebSocketInspector = obsWebSocketInspector;
        _clock = clock;
    }

    public DoctorReport CreateConfigurationFailureReport(
        IEnumerable<ConfigurationValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        DoctorCheckResult[] checks = errors
            .Select(error => Block(
                $"configuration.{error.Field}",
                error.Code,
                error.Message,
                "Restore the repository example configuration, keep only supported fields, and place any secret in Windows Credential Manager."))
            .ToArray();
        return new DoctorReport(1, _clock.UtcNow, checks);
    }

    public async Task<DoctorReport> RunAsync(
        ArenaLocalConfiguration configuration,
        ProviderConfigurationLoadResult providersResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(providersResult);

        List<DoctorCheckResult> checks = [
            Pass(
                "configuration.arena-local",
                "Arena local configuration satisfied the Phase 01 schema and path policy."),
        ];
        AddProviderConfigurationCheck(providersResult, checks);

        HostProbeResult host = await _systemProbe.GetHostAsync(cancellationToken);
        checks.Add(EvaluateHost(host));
        checks.Add(await EvaluateExecutableAsync("powershell", "pwsh", 7, cancellationToken));
        checks.Add(await EvaluateExecutableAsync("dotnet", "dotnet", 8, cancellationToken));
        checks.Add(await EvaluateExecutableAsync("node", "node", 20, cancellationToken));

        checks.Add(await EvaluateRuntimeDirectoriesAsync(configuration, cancellationToken));
        checks.Add(await EvaluateOpenTtdFilesAsync(configuration, cancellationToken));
        checks.Add(await EvaluateFileVersionAsync("openttd", configuration.OpenTtd.Executable, 14, cancellationToken));
        checks.Add(EvaluateRuntimePackages(configuration));
        checks.Add(await EvaluatePortAsync(
            "network.game-port",
            configuration.Network.BindAddress,
            ArenaRuntimeLayout.GameServerPort,
            "The generated OpenTTD game server port is available on loopback.",
            cancellationToken));
        checks.Add(await EvaluatePortAsync(
            "network.admin-port",
            configuration.Network.BindAddress,
            configuration.OpenTtd.AdminPort,
            "The configured OpenTTD AdminPort is available on loopback.",
            cancellationToken));
        checks.Add(await EvaluateDiskSpaceAsync(configuration, cancellationToken));

        checks.Add(await EvaluateExecutableAsync("obs", configuration.Obs.Executable, 28, cancellationToken));
        checks.Add(EvaluateObsTemplate(configuration));
        await AddObsCredentialAndWebSocketChecksAsync(configuration, checks, cancellationToken);
        await AddProviderCredentialChecksAsync(providersResult, checks, cancellationToken);

        checks.Add(Warning(
            "scenario-schema",
            "Scenario schema validation is deferred until Phase 07 introduces published scenario contracts.",
            "Do not treat an absent scenario schema as a benchmark-ready configuration."));
        checks.Add(Warning(
            "adminport-handshake",
            "AdminPort authentication and protocol compatibility are deferred until Phase 03.",
            "Use the Phase 02 smoke command for isolated lifecycle verification; Phase 03 adds the authenticated protocol check."));

        return new DoctorReport(1, _clock.UtcNow, checks);
    }

    private static void AddProviderConfigurationCheck(
        ProviderConfigurationLoadResult providersResult,
        List<DoctorCheckResult> checks)
    {
        if (providersResult.Succeeded)
        {
            checks.Add(Pass(
                "configuration.providers-local",
                "Provider local configuration satisfied the Phase 01 schema and contains credential references only."));
            return;
        }

        foreach (ConfigurationValidationError error in providersResult.Errors)
        {
            checks.Add(Block(
                $"configuration.providers.{error.Field}",
                error.Code,
                error.Message,
                "Restore the provider example, keep only credential_ref fields, and put secrets in Windows Credential Manager."));
        }
    }

    private static DoctorCheckResult EvaluateHost(HostProbeResult host)
    {
        if (!host.IsWindows || !host.Is64Bit || host.WindowsBuild < 22000)
        {
            return Block(
                "host",
                ArenaErrorCodes.DoctorPrerequisiteFailed,
                "OpenTTD Model Arena Phase 01 requires a 64-bit Windows 11 host.",
                "Use a supported Windows 11 64-bit account; Windows 10 and non-Windows hosts are not supported production targets.");
        }

        return Pass("host", "Windows 11 64-bit host detected.");
    }

    private async Task<DoctorCheckResult> EvaluateExecutableAsync(
        string id,
        string executable,
        int minimumMajorVersion,
        CancellationToken cancellationToken)
    {
        ExecutableProbeResult probe = await _systemProbe.ProbeExecutableAsync(executable, "--version", cancellationToken);
        if (!probe.IsAvailable || probe.Version is null || probe.Version.Major < minimumMajorVersion)
        {
            string displayName = id switch
            {
                "powershell" => "PowerShell 7 or later",
                "dotnet" => ".NET 8 SDK or later",
                "node" => "Node.js 20 or later",
                "openttd" => "OpenTTD 14 or later",
                "obs" => "OBS Studio 28 or later",
                _ => id,
            };
            string remediation = id == "obs"
                ? "Install OBS Studio or set obs.executable in arena.local.yaml to obs64.exe (for example, C:\\Program Files\\obs-studio\\bin\\64bit\\obs64.exe), then rerun doctor."
                : $"Install or configure {displayName}, then rerun bootstrap and doctor.";
            return Block(
                $"executable.{id}",
                ArenaErrorCodes.DoctorPrerequisiteFailed,
                $"{displayName} was not discovered at a supported version.",
                remediation);
        }

        return Pass($"executable.{id}", $"{id} version {probe.Version} satisfies the supported minimum.");
    }

    private async Task<DoctorCheckResult> EvaluateFileVersionAsync(
        string id,
        string executable,
        int minimumMajorVersion,
        CancellationToken cancellationToken)
    {
        ExecutableProbeResult probe = await _systemProbe.ProbeFileVersionAsync(executable, cancellationToken);
        if (!probe.IsAvailable || probe.Version is null || probe.Version.Major < minimumMajorVersion)
        {
            return Block(
                $"executable.{id}",
                ArenaErrorCodes.DoctorPrerequisiteFailed,
                "OpenTTD 14 or later was not discovered from the isolated executable's file version.",
                "Rerun bootstrap with -OpenTtdSource pointing to a supported OpenTTD installation; do not use the normal OpenTTD profile.");
        }

        return Pass($"executable.{id}", $"{id} version {probe.Version} satisfies the supported minimum.");
    }

    private async Task<DoctorCheckResult> EvaluateRuntimeDirectoriesAsync(
        ArenaLocalConfiguration configuration,
        CancellationToken cancellationToken)
    {
        (string Name, string Path)[] directories =
        [
            ("runtime", configuration.Runtime.Root),
            ("runs", configuration.Runtime.Runs),
            ("recordings", configuration.Runtime.Recordings),
        ];
        foreach ((string name, string path) in directories)
        {
            PathProbeResult probe = await _systemProbe.CheckDirectoryWritableAsync(path, cancellationToken);
            if (!probe.IsAvailable)
            {
                return Block(
                    "runtime.directories",
                    ArenaErrorCodes.DoctorPathNotWritable,
                    $"The repository-local {name} directory is missing or not writable.",
                    "Run bootstrap, then grant the repository user write access to .runtime without moving it outside the checkout.");
            }
        }

        return Pass("runtime.directories", "Repository-local runtime, run, and recording directories are writable.");
    }

    private async Task<DoctorCheckResult> EvaluateOpenTtdFilesAsync(
        ArenaLocalConfiguration configuration,
        CancellationToken cancellationToken)
    {
        (string Name, string Path)[] files =
        [
            ("OpenTTD executable", configuration.OpenTtd.Executable),
            ("server configuration", configuration.OpenTtd.ServerConfiguration),
            ("spectator configuration", configuration.OpenTtd.SpectatorConfiguration),
        ];
        foreach ((string name, string path) in files)
        {
            PathProbeResult probe = await _systemProbe.CheckFileReadableAsync(path, cancellationToken);
            if (!probe.IsAvailable)
            {
                return Block(
                    "openttd.files",
                    ArenaErrorCodes.DoctorPrerequisiteFailed,
                    $"The isolated {name} is missing or unreadable.",
                    "Rerun bootstrap with -OpenTtdSource pointing to a supported OpenTTD installation; do not use the normal OpenTTD profile.");
            }
        }

        return Pass("openttd.files", "The isolated OpenTTD executable and generated configuration are readable.");
    }

    private static DoctorCheckResult EvaluateRuntimePackages(ArenaLocalConfiguration configuration)
    {
        try
        {
            RuntimeLayoutInspection inspection = RuntimeLayoutInspector.Inspect(
                configuration.Runtime.Root,
                configuration.Network.BindAddress,
                configuration.OpenTtd.AdminPort);
            return inspection.IsValid
                ? Pass("openttd.packages", "ArenaGS, ModelProxyAI, generated configuration, and content manifest hashes are internally consistent.")
                : Block(
                    "openttd.packages",
                    ArenaErrorCodes.RuntimeLayoutInvalid,
                    "The isolated runtime is missing or has altered required GameScript, AI, configuration, or manifest files.",
                    "Rerun bootstrap from a complete checkout to regenerate the isolated runtime.");
        }
        catch (IOException)
        {
            return Block(
                "openttd.packages",
                ArenaErrorCodes.RuntimeLayoutInvalid,
                "The isolated runtime could not be inspected.",
                "Close programs using .runtime, verify its permissions, and rerun bootstrap.");
        }
        catch (UnauthorizedAccessException)
        {
            return Block(
                "openttd.packages",
                ArenaErrorCodes.RuntimeLayoutInvalid,
                "The isolated runtime could not be inspected.",
                "Grant the repository user read access to .runtime and rerun bootstrap.");
        }
    }

    private async Task<DoctorCheckResult> EvaluatePortAsync(
        string id,
        string bindAddress,
        int port,
        string successSummary,
        CancellationToken cancellationToken)
    {
        PortProbeResult probe = await _systemProbe.CheckLoopbackPortAvailableAsync(
            bindAddress,
            port,
            cancellationToken);
        return probe.IsAvailable
            ? Pass(id, successSummary)
            : Block(
                id,
                ArenaErrorCodes.DoctorPortUnavailable,
                "A generated OpenTTD listener port is unavailable on the loopback interface.",
                "Stop the process using the configured port or select an unused AdminPort in arena.local.yaml.");
    }

    private async Task<DoctorCheckResult> EvaluateDiskSpaceAsync(
        ArenaLocalConfiguration configuration,
        CancellationToken cancellationToken)
    {
        DiskProbeResult probe = await _systemProbe.GetDiskSpaceAsync(configuration.Runtime.Recordings, cancellationToken);
        long requiredBytes = configuration.Doctor.MinimumFreeDiskGigabytes * BytesPerGigabyte;
        if (!probe.IsAvailable || probe.AvailableBytes < requiredBytes)
        {
            return Block(
                "disk-space",
                ArenaErrorCodes.DoctorDiskSpaceLow,
                $"The recording drive has less than the configured {configuration.Doctor.MinimumFreeDiskGigabytes} GiB free-space threshold.",
                "Free disk space or raise only a deliberate recording-capacity threshold in arena.local.yaml.");
        }

        return Pass("disk-space", "The recording drive satisfies the configured free-space threshold.");
    }

    private static DoctorCheckResult EvaluateObsTemplate(ArenaLocalConfiguration configuration)
    {
        string templatePath = Path.Combine(
            configuration.Runtime.Root,
            ArenaRuntimeLayout.ObsDirectoryName,
            ObsSceneTemplateGenerator.TemplateFileName);
        ObsSceneTemplateValidation validation = ObsSceneTemplateGenerator.ValidateFile(
            configuration.Runtime.Root,
            templatePath);
        return validation.IsValid
            ? Pass("obs.template", "The generated OBS scene template includes all required scenes and sources.")
            : Block(
                "obs.template",
                ArenaErrorCodes.ObsTemplateInvalid,
                "The generated OBS scene template is missing required scenes or sources.",
                "Rerun bootstrap to regenerate the template, then import or recreate its required scene names and sources in OBS.");
    }

    private async Task AddObsCredentialAndWebSocketChecksAsync(
        ArenaLocalConfiguration configuration,
        List<DoctorCheckResult> checks,
        CancellationToken cancellationToken)
    {
        CredentialReadResult credential = await _credentialStore.ReadAsync(
            configuration.Obs.CredentialReference,
            cancellationToken);
        if (!credential.Succeeded || credential.Secret is null)
        {
            checks.Add(Block(
                "credential.obs",
                credential.ErrorCode ?? ArenaErrorCodes.CredentialMissing,
                "The dedicated OBS credential reference cannot be resolved.",
                "Set a dedicated OBS WebSocket password with credentials set, then rerun doctor."));
            checks.Add(Warning(
                "obs.websocket",
                "OBS WebSocket authentication was not attempted because the credential is unavailable.",
                "Resolve the dedicated OBS credential, then rerun doctor."));
            return;
        }

        checks.Add(Pass("credential.obs", "The dedicated OBS credential reference resolves without exposing its value."));
        SecretMaterial secret = credential.Secret;
        try
        {
            ObsWebSocketInspectionResult inspection = await _obsWebSocketInspector.InspectAsync(
                new ObsWebSocketInspectionRequest(
                    configuration.Obs.Host,
                    configuration.Obs.Port,
                    secret.Bytes,
                    configuration.Obs.SceneCollection),
                cancellationToken);
            if (!inspection.Succeeded || inspection.Inventory is null)
            {
                checks.Add(Block(
                    "obs.websocket",
                    inspection.ErrorCode ?? ArenaErrorCodes.ObsWebSocketUnavailable,
                    inspection.UserMessage,
                    "Start OBS with WebSocket authentication bound to loopback and confirm the configured host, port, and dedicated credential."));
                return;
            }

            ObsSceneTemplateValidation inventoryValidation = ArenaObsSceneRequirements.ValidateInventory(inspection.Inventory);
            checks.Add(inventoryValidation.IsValid
                ? Pass("obs.websocket", "OBS WebSocket authentication succeeded and the active collection includes required scenes and sources.")
                : Block(
                    "obs.websocket",
                    ArenaErrorCodes.ObsSceneRequirementsMissing,
                    "OBS authenticated but the active collection is missing required Arena scene names or sources.",
                    "Import the generated Arena template or recreate the required scenes and sources, then rerun doctor."));
        }
        finally
        {
            secret.Dispose();
        }
    }

    private async Task AddProviderCredentialChecksAsync(
        ProviderConfigurationLoadResult providersResult,
        List<DoctorCheckResult> checks,
        CancellationToken cancellationToken)
    {
        if (!providersResult.Succeeded || providersResult.Configuration is null)
        {
            return;
        }

        ProviderLocalConfiguration[] providersWithCredentials = providersResult.Configuration.Providers.Values
            .Where(provider => provider.CredentialReference is not null)
            .OrderBy(provider => provider.Id, StringComparer.Ordinal)
            .ToArray();
        if (providersWithCredentials.Length == 0)
        {
            checks.Add(Warning(
                "credential.providers",
                "No live provider credential references are configured; replay-only work remains available.",
                "Add credential_ref entries only when Phase 05 provider integration is needed."));
            return;
        }

        foreach (ProviderLocalConfiguration provider in providersWithCredentials)
        {
            CredentialReadResult credential = await _credentialStore.ReadAsync(provider.CredentialReference!, cancellationToken);
            try
            {
                if (!credential.Succeeded || credential.Secret is null)
                {
                    checks.Add(Block(
                        $"credential.provider.{provider.Id}",
                        credential.ErrorCode ?? ArenaErrorCodes.CredentialMissing,
                        $"The credential reference for configured provider '{provider.Id}' cannot be resolved.",
                        "Set the provider credential in Windows Credential Manager, or remove the provider entry until it is needed."));
                }
                else
                {
                    checks.Add(Pass(
                        $"credential.provider.{provider.Id}",
                        $"The credential reference for configured provider '{provider.Id}' resolves without exposing its value."));
                }
            }
            finally
            {
                credential.Secret?.Dispose();
            }
        }

        checks.Add(Warning(
            "provider-authentication",
            "Remote provider authentication and model requests are deferred until Phase 05.",
            "A resolved credential reference is not evidence of a successful provider request."));
    }

    private static DoctorCheckResult Pass(string id, string summary) =>
        new(
            id,
            DoctorCheckStatus.Pass,
            ArenaErrorCodes.DoctorCheckPassed,
            summary,
            "No action required.");

    private static DoctorCheckResult Warning(string id, string summary, string remediation) =>
        new(
            id,
            DoctorCheckStatus.Warning,
            ArenaErrorCodes.DoctorDeferred,
            summary,
            remediation);

    private static DoctorCheckResult Block(string id, string code, string summary, string remediation) =>
        new(id, DoctorCheckStatus.BlockingFailure, code, summary, remediation);
}
