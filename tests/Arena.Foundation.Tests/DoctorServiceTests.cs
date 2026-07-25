using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Obs;
using OpenTtd.ModelArena.Orchestrator;
using OpenTtd.ModelArena.Storage;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class DoctorServiceTests
{
    [Fact]
    public async Task ReportsWarningsForDeferredPhasesWithoutBlockingAHealthyPhaseOneSetup()
    {
        using TemporaryDirectory directory = new();
        ArenaLocalConfiguration configuration = await CreateConfigurationAsync(directory);
        DoctorService doctor = CreateDoctor(new FakeSystemProbe(true), new FakeCredentialStore(true));

        DoctorReport report = await doctor.RunAsync(configuration, EmptyProviders(), CancellationToken.None);

        Assert.False(report.HasBlockingFailures);
        Assert.Contains(report.Checks, check => check.Id == "scenario-schema" && check.Status == DoctorCheckStatus.Warning);
        DoctorCheckResult adminPortWarning = Assert.Single(report.Checks, check => check.Id == "adminport-handshake");
        Assert.Equal(DoctorCheckStatus.Warning, adminPortWarning.Status);
        Assert.Contains("bridge-smoke", adminPortWarning.Remediation, StringComparison.Ordinal);
        Assert.Contains(report.Checks, check => check.Id == "credential.adminport" && check.Status == DoctorCheckStatus.Pass);
        Assert.Contains(report.Checks, check => check.Id == "obs.websocket" && check.Status == DoctorCheckStatus.Pass);
    }

    [Fact]
    public async Task ReportsAnActionableBlockingFailureForAnUnsupportedOpenTtdExecutable()
    {
        using TemporaryDirectory directory = new();
        ArenaLocalConfiguration configuration = await CreateConfigurationAsync(directory);
        DoctorService doctor = CreateDoctor(new FakeSystemProbe(false), new FakeCredentialStore(true));

        DoctorReport report = await doctor.RunAsync(configuration, EmptyProviders(), CancellationToken.None);

        DoctorCheckResult failure = Assert.Single(report.Checks, check => check.Id == "executable.openttd");
        Assert.Equal(DoctorCheckStatus.BlockingFailure, failure.Status);
        Assert.Equal(ArenaErrorCodes.DoctorPrerequisiteFailed, failure.Code);
        Assert.False(string.IsNullOrWhiteSpace(failure.Remediation));
    }

    [Fact]
    public async Task DoesNotAttemptObsAuthenticationWhenTheDedicatedCredentialIsMissing()
    {
        using TemporaryDirectory directory = new();
        ArenaLocalConfiguration configuration = await CreateConfigurationAsync(directory);
        FakeObsInspector obsInspector = new();
        DoctorService doctor = CreateDoctor(new FakeSystemProbe(true), new FakeCredentialStore(false), obsInspector);

        DoctorReport report = await doctor.RunAsync(configuration, EmptyProviders(), CancellationToken.None);

        Assert.True(report.HasBlockingFailures);
        Assert.Contains(report.Checks, check => check.Id == "credential.obs" && check.Status == DoctorCheckStatus.BlockingFailure);
        Assert.Contains(report.Checks, check => check.Id == "obs.websocket" && check.Status == DoctorCheckStatus.Warning);
        Assert.False(obsInspector.WasCalled);
    }

    [Fact]
    public async Task ReportsBlockingFailuresForUnavailableGameAndAdminPorts()
    {
        using TemporaryDirectory directory = new();
        ArenaLocalConfiguration configuration = await CreateConfigurationAsync(directory);
        DoctorService doctor = CreateDoctor(
            new FakeSystemProbe(openTtdIsAvailable: true, portsAreAvailable: false),
            new FakeCredentialStore(true));

        DoctorReport report = await doctor.RunAsync(configuration, EmptyProviders(), CancellationToken.None);

        DoctorCheckResult[] failures = report.Checks
            .Where(check => check.Id is "network.game-port" or "network.admin-port")
            .ToArray();
        Assert.Equal(2, failures.Length);
        Assert.All(failures, failure =>
        {
            Assert.Equal(DoctorCheckStatus.BlockingFailure, failure.Status);
            Assert.Equal(ArenaErrorCodes.DoctorPortUnavailable, failure.Code);
            Assert.False(string.IsNullOrWhiteSpace(failure.Remediation));
        });
    }

    [Fact]
    public async Task ReportsAnActionableBlockingFailureForAnUnavailableObsExecutable()
    {
        using TemporaryDirectory directory = new();
        ArenaLocalConfiguration configuration = await CreateConfigurationAsync(directory);
        DoctorService doctor = CreateDoctor(
            new FakeSystemProbe(openTtdIsAvailable: true, obsIsAvailable: false),
            new FakeCredentialStore(true));

        DoctorReport report = await doctor.RunAsync(configuration, EmptyProviders(), CancellationToken.None);

        DoctorCheckResult failure = Assert.Single(report.Checks, check => check.Id == "executable.obs");
        Assert.Equal(DoctorCheckStatus.BlockingFailure, failure.Status);
        Assert.Equal(ArenaErrorCodes.DoctorPrerequisiteFailed, failure.Code);
        Assert.False(string.IsNullOrWhiteSpace(failure.Remediation));
    }

    [Fact]
    public async Task ReportsAnActionableBlockingFailureForAnUnavailableObsWebSocket()
    {
        using TemporaryDirectory directory = new();
        ArenaLocalConfiguration configuration = await CreateConfigurationAsync(directory);
        DoctorService doctor = CreateDoctor(
            new FakeSystemProbe(true),
            new FakeCredentialStore(true),
            new UnavailableObsInspector());

        DoctorReport report = await doctor.RunAsync(configuration, EmptyProviders(), CancellationToken.None);

        DoctorCheckResult failure = Assert.Single(report.Checks, check => check.Id == "obs.websocket");
        Assert.Equal(DoctorCheckStatus.BlockingFailure, failure.Status);
        Assert.Equal(ArenaErrorCodes.ObsWebSocketUnavailable, failure.Code);
        Assert.False(string.IsNullOrWhiteSpace(failure.Remediation));
    }

    private static DoctorService CreateDoctor(
        IDoctorSystemProbe systemProbe,
        ICredentialStore credentialStore,
        IObsWebSocketInspector? obsInspector = null) =>
        new(
            systemProbe,
            credentialStore,
            obsInspector ?? new FakeObsInspector(),
            new FixedDoctorClock());

    private static ProviderConfigurationLoadResult EmptyProviders() =>
        new(
            new ProviderLocalConfigurationSet(
                "providers.local.yaml",
                new Dictionary<string, ProviderLocalConfiguration>(StringComparer.Ordinal)),
            []);

    private static async Task<ArenaLocalConfiguration> CreateConfigurationAsync(TemporaryDirectory directory)
    {
        string runtimeRoot = directory.CreateDirectory(".runtime");
        directory.WriteFile("openttd/game/ArenaGS/main.nut", "class ArenaGS {}");
        directory.WriteFile("openttd/game/ArenaGS/info.nut", $"ArenaGS function GetShortName() {{ return \"ARGS\"; }} function GetAPIVersion() {{ return \"{ArenaRuntimeLayout.ArenaGameScriptApiVersion}\"; }} RegisterGS");
        directory.WriteFile("openttd/ai/ModelProxyAI/main.nut", "class ModelProxyAI {}");
        directory.WriteFile("openttd/ai/ModelProxyAI/info.nut", "ModelProxyAI function GetShortName() { return \"MPAI\"; } function GetAPIVersion() { return \"1.0\"; } RegisterAI");
        RuntimeLayoutResult runtime = await RuntimeLayoutBuilder.PrepareAsync(
            new RuntimeLayoutRequest(
                directory.Path,
                runtimeRoot,
                null,
                "127.0.0.1",
                3977),
            CancellationToken.None);
        if (!runtime.Succeeded)
        {
            throw new InvalidOperationException("Test runtime could not be prepared.");
        }

        string openttdRoot = Path.Combine(runtimeRoot, ArenaRuntimeLayout.OpenTtdDirectoryName);
        directory.WriteFile(".runtime/openttd/openttd.exe", "test executable");
        await ObsSceneTemplateGenerator.WriteAsync(
            runtimeRoot,
            Path.Combine(runtimeRoot, "obs", ObsSceneTemplateGenerator.TemplateFileName),
            CancellationToken.None);

        if (!CredentialReference.TryParse("credman:OpenTTDModelArena/OBS", out CredentialReference? credential) || credential is null)
        {
            throw new InvalidOperationException("Test credential reference did not parse.");
        }

        if (!CredentialReference.TryParse("credman:OpenTTDModelArena/AdminPort", out CredentialReference? adminCredential) || adminCredential is null)
        {
            throw new InvalidOperationException("Test AdminPort credential reference did not parse.");
        }

        return new ArenaLocalConfiguration(
            directory.Path,
            Path.Combine(directory.Path, ".config", "arena.local.yaml"),
            new RuntimeLocalConfiguration(runtimeRoot, Path.Combine(runtimeRoot, "runs"), Path.Combine(runtimeRoot, "recordings")),
            new OpenTtdLocalConfiguration(
                Path.Combine(openttdRoot, "openttd.exe"),
                Path.Combine(openttdRoot, "server.cfg"),
                Path.Combine(openttdRoot, "spectator.cfg"),
                3977,
                adminCredential),
            new ObsLocalConfiguration("127.0.0.1", 4455, credential, "OpenTTD Model Arena", "obs64"),
            new NetworkLocalConfiguration("127.0.0.1"),
            new LoggingLocalConfiguration("Information", true),
            new DoctorLocalConfiguration(20));
    }

    private sealed class FixedDoctorClock : IDoctorClock
    {
        public DateTimeOffset UtcNow => new(2026, 7, 23, 10, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeSystemProbe : IDoctorSystemProbe
    {
        private readonly bool _openTtdIsAvailable;
        private readonly bool _obsIsAvailable;
        private readonly bool _portsAreAvailable;

        public FakeSystemProbe(
            bool openTtdIsAvailable,
            bool obsIsAvailable = true,
            bool portsAreAvailable = true)
        {
            _openTtdIsAvailable = openTtdIsAvailable;
            _obsIsAvailable = obsIsAvailable;
            _portsAreAvailable = portsAreAvailable;
        }

        public Task<HostProbeResult> GetHostAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HostProbeResult(true, true, 22631));

        public Task<ExecutableProbeResult> ProbeExecutableAsync(
            string executable,
            string versionArgument,
            CancellationToken cancellationToken)
        {
            bool isOpenTtd = executable.EndsWith("openttd.exe", StringComparison.OrdinalIgnoreCase);
            if (isOpenTtd && !_openTtdIsAvailable)
            {
                return Task.FromResult(new ExecutableProbeResult(false, null, "test-missing"));
            }

            if (string.Equals(executable, "obs64", StringComparison.Ordinal) && !_obsIsAvailable)
            {
                return Task.FromResult(new ExecutableProbeResult(false, null, "test-missing"));
            }

            Version version = executable switch
            {
                "pwsh" => new Version(7, 4, 0),
                "dotnet" => new Version(8, 0, 100),
                "node" => new Version(22, 0, 0),
                "obs64" => new Version(30, 0, 0),
                _ => new Version(14, 0, 0),
            };
            return Task.FromResult(new ExecutableProbeResult(true, version, null));
        }

        public Task<ExecutableProbeResult> ProbeFileVersionAsync(
            string executable,
            CancellationToken cancellationToken) =>
            Task.FromResult(_openTtdIsAvailable
                ? new ExecutableProbeResult(true, new Version(14, 0, 0), null)
                : new ExecutableProbeResult(false, null, "test-missing"));

        public Task<PathProbeResult> CheckFileReadableAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new PathProbeResult(true, null));

        public Task<PathProbeResult> CheckDirectoryWritableAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new PathProbeResult(true, null));

        public Task<PortProbeResult> CheckLoopbackPortAvailableAsync(
            string address,
            int port,
            CancellationToken cancellationToken) =>
            Task.FromResult(_portsAreAvailable
                ? new PortProbeResult(true, null)
                : new PortProbeResult(false, "test-in-use"));

        public Task<DiskProbeResult> GetDiskSpaceAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new DiskProbeResult(true, 100L * 1024 * 1024 * 1024, null));
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly bool _isAvailable;

        public FakeCredentialStore(bool isAvailable)
        {
            _isAvailable = isAvailable;
        }

        public Task<CredentialOperationResult> SetAsync(
            CredentialReference reference,
            SecretMaterial secret,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CredentialOperationResult(true, null, "saved"));

        public Task<CredentialReadResult> ReadAsync(
            CredentialReference reference,
            CancellationToken cancellationToken)
        {
            if (!_isAvailable)
            {
                return Task.FromResult(new CredentialReadResult(
                    false,
                    null,
                    ArenaErrorCodes.CredentialMissing,
                    "missing"));
            }

            return Task.FromResult(new CredentialReadResult(
                true,
                SecretMaterial.FromUtf8("x"),
                null,
                "available"));
        }

        public Task<CredentialOperationResult> RemoveAsync(
            CredentialReference reference,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CredentialOperationResult(true, null, "removed"));

        public Task<CredentialListResult> ListArenaMetadataAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CredentialListResult(true, [], null, "listed"));
    }

    private sealed class FakeObsInspector : IObsWebSocketInspector
    {
        public bool WasCalled { get; private set; }

        public Task<ObsWebSocketInspectionResult> InspectAsync(
            ObsWebSocketInspectionRequest request,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            IReadOnlyDictionary<string, IReadOnlyList<string>> scenes =
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["Arena - Starting"] = ["Arena-Wide", "Arena-Sidebar"],
                    ["Arena - Wide"] = ["Arena-Wide", "Arena-Sidebar"],
                    ["Arena - Medium"] = ["Arena-Medium", "Arena-Sidebar"],
                    ["Arena - Close"] = ["Arena-Close", "Arena-Sidebar"],
                    ["Arena - Results"] = ["Arena-Results"],
                    ["Arena - Failure"] = ["Arena-Results"],
                };
            return Task.FromResult(new ObsWebSocketInspectionResult(
                true,
                new ObsSceneInventory(scenes),
                null,
                "available"));
        }
    }

    private sealed class UnavailableObsInspector : IObsWebSocketInspector
    {
        public Task<ObsWebSocketInspectionResult> InspectAsync(
            ObsWebSocketInspectionRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ObsWebSocketInspectionResult(
                false,
                null,
                ArenaErrorCodes.ObsWebSocketUnavailable,
                "OBS WebSocket is unavailable for this test."));
    }
}
