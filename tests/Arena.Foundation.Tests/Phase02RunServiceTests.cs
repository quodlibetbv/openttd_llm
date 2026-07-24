using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Orchestrator;
using OpenTtd.ModelArena.Storage;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class Phase02RunServiceTests
{
    [Fact]
    public async Task CompletesAProviderFreeSmokeRunWithIsolatedArtifacts()
    {
        using TemporaryDirectory directory = new();
        ArenaLocalConfiguration configuration = await CreateConfigurationAsync(directory);
        FakeProcessFactory processes = new();
        FakeConsoleBridge console = new(processes);
        Phase02RunService service = CreateService(processes, console, portIsReady: true);

        ArenaRunResult result = await service.RunSmokeAsync(
            configuration,
            SmokeOptions(runDuration: TimeSpan.Zero),
            CancellationToken.None);

        string runDirectory = Path.Combine(configuration.Runtime.Runs, result.RunId);
        string cachePath = Phase02RunPreparation.GetStartingSaveCachePath(configuration);
        Assert.Equal(ArenaRunState.Completed, result.FinalState);
        Assert.Equal(ArenaRunExitReason.Completed, result.ExitReason);
        Assert.Equal(4, result.Components.Count);
        Assert.True(File.Exists(Path.Combine(runDirectory, "input", "starting-save.sav")));
        Assert.True(File.Exists(Path.Combine(runDirectory, "checkpoints", "checkpoint-0001.sav")));
        Assert.True(File.Exists(Path.Combine(runDirectory, "final-save.sav")));
        Assert.True(File.Exists(Path.Combine(runDirectory, "run-result.json")));
        Assert.Equal(ArenaRunState.Completed, RunLifecycleJournal.ReadLatest(runDirectory)?.State);
        Assert.True(File.Exists(cachePath));
        Assert.Equal(ComputeSha256(cachePath), ComputeSha256(Path.Combine(runDirectory, "input", "starting-save.sav")));
        Assert.Empty(Directory.EnumerateFiles(runDirectory, ArenaRuntimeLayout.SecretsConfigurationFileName, SearchOption.AllDirectories));
        Assert.DoesNotContain(result.Artifacts, artifact => artifact.Path.EndsWith("secrets.cfg", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(console.Commands, command => command.Operation == OpenTtdConsoleOperation.Unpause);
        Assert.All(processes.Started.Where(process => process.ComponentId != "template-server"), process => Assert.True(process.HasExited));

        string serverConfiguration = File.ReadAllText(Path.Combine(runDirectory, "server", "openttd.cfg"));
        string wideConfiguration = File.ReadAllText(Path.Combine(runDirectory, "spectators", "spectator-wide", "openttd.cfg"));
        Assert.Contains("[game_scripts]", serverConfiguration, StringComparison.Ordinal);
        Assert.Contains("ArenaGS =", serverConfiguration, StringComparison.Ordinal);
        Assert.Contains("ModelProxyAI =", serverConfiguration, StringComparison.Ordinal);
        Assert.Contains("max_no_competitors = 1", serverConfiguration, StringComparison.Ordinal);
        Assert.Contains("client_name = Arena-Wide", wideConfiguration, StringComparison.Ordinal);
        Assert.DoesNotContain("{{client_name}}", wideConfiguration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreservesTheCheckpointWhenTheServerExitsDuringTheRun()
    {
        using TemporaryDirectory directory = new();
        ArenaLocalConfiguration configuration = await CreateConfigurationAsync(directory);
        FakeProcessFactory processes = new();
        FakeConsoleBridge console = new(processes) { ExitServerWhenUnpaused = true };
        Phase02RunService service = CreateService(processes, console, portIsReady: true);

        ArenaRunResult result = await service.RunSmokeAsync(
            configuration,
            SmokeOptions(runDuration: TimeSpan.Zero),
            CancellationToken.None);

        string runDirectory = Path.Combine(configuration.Runtime.Runs, result.RunId);
        Assert.Equal(ArenaRunState.Failed, result.FinalState);
        Assert.Equal(ArenaRunExitReason.ServerExited, result.ExitReason);
        Assert.Equal(ArenaErrorCodes.RunServerExited, result.ErrorCode);
        Assert.True(File.Exists(Path.Combine(runDirectory, "checkpoints", "checkpoint-0001.sav")));
        Assert.True(File.Exists(Path.Combine(runDirectory, "component-logs", "server.stdout.log")));
        Assert.Equal(ArenaRunState.Failed, RunLifecycleJournal.ReadLatest(runDirectory)?.State);
    }

    [Fact]
    public async Task ClassifiesAStartupTimeoutWithoutLeakingTheServer()
    {
        using TemporaryDirectory directory = new();
        ArenaLocalConfiguration configuration = await CreateConfigurationAsync(directory);
        FakeProcessFactory processes = new();
        FakeConsoleBridge console = new(processes);
        Phase02RunService service = CreateService(processes, console, portIsReady: false);

        ArenaRunResult result = await service.RunSmokeAsync(
            configuration,
            SmokeOptions(runDuration: TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(ArenaRunState.Failed, result.FinalState);
        Assert.Equal(ArenaRunExitReason.StartupTimedOut, result.ExitReason);
        Assert.Equal(ArenaErrorCodes.RunStartupTimedOut, result.ErrorCode);
        Assert.All(processes.Started, process => Assert.True(process.HasExited));
    }

    [Fact]
    public async Task ClassifiesMissingExplicitReadinessSignalsWithoutLeakingTheTemplateServer()
    {
        using TemporaryDirectory directory = new();
        ArenaLocalConfiguration configuration = await CreateConfigurationAsync(directory);
        FakeProcessFactory processes = new();
        FakeConsoleBridge console = new(processes) { SignalsAreReady = false };
        Phase02RunService service = CreateService(processes, console, portIsReady: true);

        ArenaRunResult result = await service.RunSmokeAsync(
            configuration,
            SmokeOptions(runDuration: TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(ArenaRunState.Failed, result.FinalState);
        Assert.Equal(ArenaRunExitReason.GameScriptNotReady, result.ExitReason);
        Assert.Equal(ArenaErrorCodes.RunGameScriptNotReady, result.ErrorCode);
        Assert.All(processes.Started, process => Assert.True(process.HasExited));
    }

    [Fact]
    public async Task ClassifiesASpectatorCrashSeparatelyFromTheServer()
    {
        using TemporaryDirectory directory = new();
        ArenaLocalConfiguration configuration = await CreateConfigurationAsync(directory);
        FakeProcessFactory processes = new() { ExitComponentOnStart = "spectator-medium" };
        FakeConsoleBridge console = new(processes);
        Phase02RunService service = CreateService(processes, console, portIsReady: true);

        ArenaRunResult result = await service.RunSmokeAsync(
            configuration,
            SmokeOptions(runDuration: TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(ArenaRunState.Failed, result.FinalState);
        Assert.Equal(ArenaRunExitReason.SpectatorExited, result.ExitReason);
        Assert.Equal(ArenaErrorCodes.RunSpectatorExited, result.ErrorCode);
        Assert.Contains(processes.Started, process => process.ComponentId == "server" && process.HasExited);
    }

    [Fact]
    public async Task StopsASpectatorWhoseStableCaptureWindowDoesNotAppear()
    {
        using TemporaryDirectory directory = new();
        ArenaLocalConfiguration configuration = await CreateConfigurationAsync(directory);
        FakeProcessFactory processes = new() { FailWindowTitleForComponent = "spectator-medium" };
        FakeConsoleBridge console = new(processes);
        Phase02RunService service = CreateService(processes, console, portIsReady: true);

        ArenaRunResult result = await service.RunSmokeAsync(
            configuration,
            SmokeOptions(runDuration: TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(ArenaRunState.Failed, result.FinalState);
        Assert.Equal(ArenaRunExitReason.StartupTimedOut, result.ExitReason);
        Assert.Equal(ArenaErrorCodes.RunStartupTimedOut, result.ErrorCode);
        Assert.All(processes.Started, process => Assert.True(process.HasExited));
    }

    [Fact]
    public async Task UsesForcedTerminationWhenASpectatorIgnoresGracefulShutdown()
    {
        using TemporaryDirectory directory = new();
        ArenaLocalConfiguration configuration = await CreateConfigurationAsync(directory);
        FakeProcessFactory processes = new() { IgnoreGracefulShutdownForComponent = "spectator-close" };
        FakeConsoleBridge console = new(processes);
        Phase02RunService service = CreateService(processes, console, portIsReady: true);

        ArenaRunResult result = await service.RunSmokeAsync(
            configuration,
            SmokeOptions(runDuration: TimeSpan.Zero),
            CancellationToken.None);

        FakeManagedProcess spectator = Assert.Single(
            processes.Started,
            process => process.ComponentId == "spectator-close");
        Assert.Equal(ArenaRunState.Completed, result.FinalState);
        Assert.True(spectator.ForceTerminationWasRequested);
        Assert.True(spectator.HasExited);
    }

    [Fact]
    public async Task ClassifiesCancellationAndPreservesTheLatestCheckpoint()
    {
        using TemporaryDirectory directory = new();
        ArenaLocalConfiguration configuration = await CreateConfigurationAsync(directory);
        using CancellationTokenSource cancellation = new();
        FakeProcessFactory processes = new();
        FakeConsoleBridge console = new(processes) { CancellationSource = cancellation };
        Phase02RunService service = CreateService(processes, console, portIsReady: true);

        ArenaRunResult result = await service.RunSmokeAsync(
            configuration,
            SmokeOptions(runDuration: TimeSpan.FromSeconds(5)),
            cancellation.Token);

        string runDirectory = Path.Combine(configuration.Runtime.Runs, result.RunId);
        Assert.Equal(ArenaRunState.Cancelled, result.FinalState);
        Assert.Equal(ArenaRunExitReason.Cancelled, result.ExitReason);
        Assert.Equal(ArenaErrorCodes.RunCancelled, result.ErrorCode);
        Assert.True(File.Exists(Path.Combine(runDirectory, "checkpoints", "checkpoint-0001.sav")));
        Assert.All(processes.Started, process => Assert.True(process.HasExited));
    }

    [Fact]
    public async Task RetriesATransientDedicatedConsoleAttachmentFailure()
    {
        using TemporaryDirectory directory = new();
        ArenaLocalConfiguration configuration = await CreateConfigurationAsync(directory);
        FakeProcessFactory processes = new();
        FakeConsoleBridge console = new(processes)
        {
            TransientConsoleAttachmentFailuresRemaining = 1,
        };
        Phase02RunService service = CreateService(processes, console, portIsReady: true);

        ArenaRunResult result = await service.RunSmokeAsync(
            configuration,
            SmokeOptions(runDuration: TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(ArenaRunState.Completed, result.FinalState);
        Assert.Equal(0, console.TransientConsoleAttachmentFailuresRemaining);
    }

    private static Phase02RunService CreateService(
        FakeProcessFactory processes,
        FakeConsoleBridge console,
        bool portIsReady) =>
        new(
            new RunDirectoryAllocator(new TestSuffixGenerator()),
            processes,
            console,
            new FakeReadinessProbe(portIsReady));

    private static Phase02SmokeOptions SmokeOptions(TimeSpan runDuration) =>
        new(TimeSpan.FromSeconds(5), runDuration, TimeSpan.FromSeconds(2));

    private static async Task<ArenaLocalConfiguration> CreateConfigurationAsync(TemporaryDirectory directory)
    {
        directory.WriteFile("openttd/game/ArenaGS/main.nut", "class ArenaGS extends GSController { function Start() {} }");
        directory.WriteFile("openttd/game/ArenaGS/info.nut", "class ArenaGSInfo extends GSInfo { function GetShortName() { return \"ARGS\"; } function GetAPIVersion() { return \"1.2\"; } } RegisterGS(ArenaGSInfo()); // ArenaGS");
        directory.WriteFile("openttd/ai/ModelProxyAI/main.nut", "class ModelProxyAI extends AIController { function Start() {} }");
        directory.WriteFile("openttd/ai/ModelProxyAI/info.nut", "class ModelProxyAIInfo extends AIInfo { function GetShortName() { return \"MPAI\"; } function GetAPIVersion() { return \"1.0\"; } } RegisterAI(ModelProxyAIInfo()); // ModelProxyAI");
        string runtimeRoot = directory.CreateDirectory(".runtime");
        RuntimeLayoutResult runtime = await RuntimeLayoutBuilder.PrepareAsync(
            new RuntimeLayoutRequest(directory.Path, runtimeRoot, null, "127.0.0.1", 3977),
            CancellationToken.None);
        Assert.True(runtime.Succeeded);
        string openTtdRoot = Path.Combine(runtimeRoot, ArenaRuntimeLayout.OpenTtdDirectoryName);
        directory.WriteFile(".runtime/openttd/openttd.exe", "test executable");
        Assert.True(CredentialReference.TryParse("credman:OpenTTDModelArena/OBS", out CredentialReference? credential));
        Assert.NotNull(credential);
        Assert.True(CredentialReference.TryParse("credman:OpenTTDModelArena/AdminPort", out CredentialReference? adminCredential));
        Assert.NotNull(adminCredential);

        return new ArenaLocalConfiguration(
            directory.Path,
            Path.Combine(directory.Path, ".config", "arena.local.yaml"),
            new RuntimeLocalConfiguration(runtimeRoot, Path.Combine(runtimeRoot, "runs"), Path.Combine(runtimeRoot, "recordings")),
            new OpenTtdLocalConfiguration(
                Path.Combine(openTtdRoot, "openttd.exe"),
                Path.Combine(openTtdRoot, ArenaRuntimeLayout.ServerConfigurationFileName),
                Path.Combine(openTtdRoot, ArenaRuntimeLayout.SpectatorConfigurationFileName),
                3977,
                adminCredential!),
            new ObsLocalConfiguration("127.0.0.1", 4455, credential!, "OpenTTD Model Arena", "obs64"),
            new NetworkLocalConfiguration("127.0.0.1"),
            new LoggingLocalConfiguration("Information", true),
            new DoctorLocalConfiguration(20));
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private sealed class TestSuffixGenerator : IRunIdSuffixGenerator
    {
        private int _next;

        public string CreateSuffix() => (++_next).ToString("x12", System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FakeReadinessProbe : ILoopbackReadinessProbe
    {
        private readonly bool _isReady;

        public FakeReadinessProbe(bool isReady)
        {
            _isReady = isReady;
        }

        public Task<bool> WaitForPortAsync(
            string address,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(_isReady);
    }

    private sealed class FakeProcessFactory : IArenaProcessFactory
    {
        private int _nextProcessId = 5000;
        private readonly Dictionary<int, FakeManagedProcess> _byId = [];

        public string? ExitComponentOnStart { get; init; }

        public string? FailWindowTitleForComponent { get; init; }

        public string? IgnoreGracefulShutdownForComponent { get; init; }

        public IReadOnlyList<FakeManagedProcess> Started => _byId.Values.OrderBy(process => process.ProcessId).ToArray();

        public Task<IManagedArenaProcess> StartAsync(OpenTtdProcessStartRequest request, CancellationToken cancellationToken)
        {
            File.WriteAllText(
                Path.Combine(request.WorkingDirectory, ArenaRuntimeLayout.SecretsConfigurationFileName),
                "generated-by-openttd");
            FakeManagedProcess process = new(
                request.ComponentId,
                ++_nextProcessId,
                request.WorkingDirectory,
                string.Equals(request.ComponentId, ExitComponentOnStart, StringComparison.Ordinal),
                string.Equals(request.ComponentId, FailWindowTitleForComponent, StringComparison.Ordinal),
                string.Equals(request.ComponentId, IgnoreGracefulShutdownForComponent, StringComparison.Ordinal));
            _byId.Add(process.ProcessId, process);
            return Task.FromResult<IManagedArenaProcess>(process);
        }

        public FakeManagedProcess Get(int processId) => _byId[processId];
    }

    private sealed class FakeConsoleBridge : IOpenTtdConsoleBridge
    {
        private readonly FakeProcessFactory _processes;

        public FakeConsoleBridge(FakeProcessFactory processes)
        {
            _processes = processes;
        }

        public bool ExitServerWhenUnpaused { get; init; }

        public CancellationTokenSource? CancellationSource { get; init; }

        public List<OpenTtdConsoleCommand> Commands { get; } = [];

        public int TransientConsoleAttachmentFailuresRemaining { get; set; }

        public bool SignalsAreReady { get; init; } = true;

        private int _serverUnpauseCount;

        public Task SendAsync(int processId, OpenTtdConsoleCommand command, CancellationToken cancellationToken)
        {
            if (TransientConsoleAttachmentFailuresRemaining > 0)
            {
                TransientConsoleAttachmentFailuresRemaining--;
                throw new OpenTtdConsoleControlException("dedicated console is still releasing a prior bridge attachment", isTransientAttachmentFailure: true);
            }

            Commands.Add(command);
            FakeManagedProcess process = _processes.Get(processId);
            switch (command.Operation)
            {
                case OpenTtdConsoleOperation.Save:
                    string savePath = Path.Combine(process.WorkingDirectory, "save", command.SaveName + ".sav");
                    string? parent = Path.GetDirectoryName(savePath);
                    Assert.NotNull(parent);
                    Directory.CreateDirectory(parent!);
                    File.WriteAllText(savePath, command.SaveName!);
                    break;
                case OpenTtdConsoleOperation.Quit:
                    process.Exit(0);
                    break;
                case OpenTtdConsoleOperation.Unpause:
                    if (process.ComponentId == "server")
                    {
                        _serverUnpauseCount++;
                        if (ExitServerWhenUnpaused && _serverUnpauseCount >= 2)
                        {
                            process.Exit(9);
                        }

                        if (_serverUnpauseCount >= 2)
                        {
                            CancellationSource?.Cancel();
                        }
                    }
                    break;
            }

            return Task.CompletedTask;
        }

        public Task<bool> WaitForSignalsAsync(
            int processId,
            IReadOnlyCollection<string> expectedSignals,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(SignalsAreReady && !_processes.Get(processId).HasExited);
    }

    private sealed class FakeManagedProcess : IManagedArenaProcess
    {
        private int? _exitCode;

        public FakeManagedProcess(
            string componentId,
            int processId,
            string workingDirectory,
            bool startsExited,
            bool failsWindowTitle,
            bool ignoresGracefulShutdown)
        {
            ComponentId = componentId;
            ProcessId = processId;
            WorkingDirectory = workingDirectory;
            FailsWindowTitle = failsWindowTitle;
            IgnoresGracefulShutdown = ignoresGracefulShutdown;
            if (startsExited)
            {
                Exit(9);
            }
        }

        public string ComponentId { get; }

        public int ProcessId { get; }

        public string WorkingDirectory { get; }

        public bool FailsWindowTitle { get; }

        public bool IgnoresGracefulShutdown { get; }

        public bool ForceTerminationWasRequested { get; private set; }

        public bool HasExited => _exitCode is not null;

        public int? ExitCode => _exitCode;

        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(HasExited);

        public Task<bool> RequestGracefulShutdownAsync(CancellationToken cancellationToken)
        {
            if (IgnoresGracefulShutdown)
            {
                return Task.FromResult(false);
            }

            Exit(0);
            return Task.FromResult(true);
        }

        public Task ForceTerminateAsync(CancellationToken cancellationToken)
        {
            ForceTerminationWasRequested = true;
            Exit(-1);
            return Task.CompletedTask;
        }

        public Task<bool> SetStableWindowTitleAsync(string title, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(!FailsWindowTitle);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Exit(int exitCode)
        {
            _exitCode ??= exitCode;
        }
    }
}
