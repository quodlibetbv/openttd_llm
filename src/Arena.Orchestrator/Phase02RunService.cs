using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Orchestrator;

public sealed record Phase02SmokeOptions(
    TimeSpan StartupTimeout,
    TimeSpan RunDuration,
    TimeSpan ShutdownTimeout)
{
    public static Phase02SmokeOptions Default { get; } = new(
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15));

    public void Validate()
    {
        if (StartupTimeout is { TotalSeconds: < 5 or > 300 } ||
            RunDuration is { TotalSeconds: < 0 or > 300 } ||
            ShutdownTimeout is { TotalSeconds: < 2 or > 120 })
        {
            throw new ArgumentOutOfRangeException(nameof(StartupTimeout), "Phase 02 smoke timeouts are outside their supported bounds.");
        }
    }
}

/// <summary>
/// Executes the provider-free Phase 02 smoke lifecycle. It intentionally owns
/// only processes, fixed-save preparation, and artifacts; AdminPort gameplay,
/// providers, scoring, OBS, and recording remain later phase boundaries.
/// </summary>
public sealed class Phase02RunService
{
    private const string ServerComponentId = "server";
    private const string TemplateServerComponentId = "template-server";
    private const string CheckpointSaveName = "checkpoint-0001";
    private const string FinalSaveName = "final-save";
    private const string TemplateSaveName = "starting-save-template";
    private const int MaximumTransientConsoleAttachmentAttempts = 5;
    private static readonly TimeSpan TransientConsoleAttachmentRetryInterval = TimeSpan.FromMilliseconds(100);
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions ResultJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private readonly RunDirectoryAllocator _runDirectoryAllocator;
    private readonly IArenaProcessFactory _processFactory;
    private readonly IOpenTtdConsoleBridge _consoleBridge;
    private readonly ILoopbackReadinessProbe _readinessProbe;
    private readonly TimeProvider _timeProvider;

    public Phase02RunService(
        RunDirectoryAllocator runDirectoryAllocator,
        IArenaProcessFactory processFactory,
        IOpenTtdConsoleBridge consoleBridge,
        ILoopbackReadinessProbe readinessProbe,
        TimeProvider? timeProvider = null)
    {
        _runDirectoryAllocator = runDirectoryAllocator ?? throw new ArgumentNullException(nameof(runDirectoryAllocator));
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        _consoleBridge = consoleBridge ?? throw new ArgumentNullException(nameof(consoleBridge));
        _readinessProbe = readinessProbe ?? throw new ArgumentNullException(nameof(readinessProbe));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ArenaRunResult> RunSmokeAsync(
        ArenaLocalConfiguration configuration,
        Phase02SmokeOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        RunDirectoryAllocation allocation = await _runDirectoryAllocator.AllocateAsync(
            configuration.Runtime.Runs,
            "smoke",
            cancellationToken);
        DateTimeOffset createdUtc = _timeProvider.GetUtcNow();
        using RunLifecycleJournal journal = new(allocation.RunId, allocation.Paths);
        Phase02RunLayout? layout = null;
        IManagedArenaProcess? server = null;
        List<IManagedArenaProcess> spectators = [];
        List<RunComponentResult> componentResults = [];
        ArenaRunExitReason exitReason = ArenaRunExitReason.Completed;
        string? errorCode = null;
        string? failureDetail = null;
        ArenaRunState currentState = ArenaRunState.Created;

        try
        {
            await journal.InitializeAsync(createdUtc, cancellationToken);
            currentState = await TransitionAsync(journal, ArenaRunState.Preparing, null, cancellationToken);
            EnsureRuntimeIsReady(configuration);
            layout = Phase02RunPreparation.CreateLayout(allocation);
            await Phase02RunPreparation.PrepareRunWorkspacesAsync(configuration, layout, cancellationToken);
            await EnsureStartingSaveAsync(configuration, layout, options, cancellationToken);

            currentState = await TransitionAsync(journal, ArenaRunState.StartingServer, ServerComponentId, cancellationToken);
            server = await StartServerAsync(configuration, layout, ServerComponentId, cancellationToken);
            currentState = await TransitionAsync(journal, ArenaRunState.WaitingForGameScript, ServerComponentId, cancellationToken);
            // ArenaGS reports the loaded fixed save while paused. The model proxy
            // then needs one controlled post-load unpause before we pause again
            // for spectator startup and the checkpoint.
            await WaitForServerReadinessAsync(
                configuration,
                server,
                options,
                cancellationToken,
                resumeModelProxyFromPausedSave: true);
            await SendConsoleCommandAsync(server, OpenTtdConsoleCommand.Pause, cancellationToken);

            currentState = await TransitionAsync(journal, ArenaRunState.StartingClients, null, cancellationToken);
            foreach (Phase02SpectatorWorkspace spectatorWorkspace in layout.Spectators.Values)
            {
                IManagedArenaProcess spectator = await StartSpectatorAsync(configuration, spectatorWorkspace, cancellationToken);
                // Register immediately so the common finalizer owns this process
                // even if its capture window never becomes available.
                spectators.Add(spectator);
                bool titleSet = await spectator.SetStableWindowTitleAsync(
                    $"{spectatorWorkspace.Definition.StableWindowTitle} [{allocation.RunId}]",
                    options.StartupTimeout,
                    cancellationToken);
                if (!titleSet)
                {
                    throw new RunFailureException(
                        ArenaRunExitReason.StartupTimedOut,
                        ArenaErrorCodes.RunStartupTimedOut,
                        spectatorWorkspace.Definition.ComponentId,
                        "A spectator process did not expose a stable capture window before the startup timeout.");
                }

                if (spectator.HasExited)
                {
                    throw ProcessExitedFailure(spectator, ArenaRunExitReason.SpectatorExited);
                }
            }

            currentState = await TransitionAsync(journal, ArenaRunState.Ready, null, cancellationToken);
            await SaveArtifactAsync(server, layout, CheckpointSaveName, layout.Allocation.Paths.Resolve(Path.Combine("checkpoints", CheckpointSaveName + ".sav")), options.StartupTimeout, cancellationToken);

            currentState = await TransitionAsync(journal, ArenaRunState.Running, null, cancellationToken);
            await SendConsoleCommandAsync(server, OpenTtdConsoleCommand.Unpause, cancellationToken);
            await WaitForSmokeDurationAsync(server, spectators, options.RunDuration, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            exitReason = ArenaRunExitReason.Cancelled;
            errorCode = ArenaErrorCodes.RunCancelled;
            failureDetail = "The caller cancelled the smoke run.";
        }
        catch (RunFailureException exception)
        {
            exitReason = exception.ExitReason;
            errorCode = exception.ErrorCode;
            failureDetail = SafeFailureDetail(exception);
        }
        catch (OpenTtdConsoleControlException exception)
        {
            exitReason = ArenaRunExitReason.PreparationFailed;
            errorCode = ArenaErrorCodes.RunConsoleControlFailed;
            failureDetail = SafeFailureDetail(exception);
        }
        catch (IOException exception)
        {
            exitReason = ArenaRunExitReason.PreparationFailed;
            errorCode = ArenaErrorCodes.RunPreparationFailed;
            failureDetail = SafeFailureDetail(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            exitReason = ArenaRunExitReason.PreparationFailed;
            errorCode = ArenaErrorCodes.RunPreparationFailed;
            failureDetail = SafeFailureDetail(exception);
        }
        catch (InvalidOperationException exception)
        {
            exitReason = ArenaRunExitReason.PreparationFailed;
            errorCode = ArenaErrorCodes.RunPreparationFailed;
            failureDetail = SafeFailureDetail(exception);
        }
        finally
        {
            try
            {
                if (currentState == ArenaRunState.Created)
                {
                    currentState = await TransitionAsync(journal, ArenaRunState.Preparing, null, CancellationToken.None);
                }

                if (currentState != ArenaRunState.Finalizing)
                {
                    currentState = await TransitionAsync(journal, ArenaRunState.Finalizing, null, CancellationToken.None);
                }

                if (layout is not null && server is not null && !server.HasExited)
                {
                    await SendConsoleCommandAsync(server, OpenTtdConsoleCommand.Pause, CancellationToken.None);
                    try
                    {
                        await SaveArtifactAsync(server, layout, FinalSaveName, layout.FinalSavePath, options.StartupTimeout, CancellationToken.None);
                    }
                    catch (RunFailureException) when (exitReason != ArenaRunExitReason.Completed)
                    {
                        // An abnormal run keeps its latest completed checkpoint and component logs.
                    }
                }

                await StopSpectatorsAsync(spectators, options.ShutdownTimeout, CancellationToken.None);
                if (server is not null)
                {
                    await StopServerAsync(server, options.ShutdownTimeout, CancellationToken.None);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (exitReason == ArenaRunExitReason.Completed)
                {
                    exitReason = ArenaRunExitReason.FinalizationFailed;
                    errorCode = ArenaErrorCodes.RunFinalizationFailed;
                }

            }

            componentResults.AddRange(CreateComponentResults(server, spectators, allocation.RunId));
            foreach (IManagedArenaProcess process in spectators)
            {
                await process.DisposeAsync();
            }

            if (server is not null)
            {
                await server.DisposeAsync();
            }

            try
            {
                if (layout is not null)
                {
                    PurgeGeneratedOpenTtdSecrets(layout);
                }
            }
            catch (Exception)
            {
                exitReason = ArenaRunExitReason.FinalizationFailed;
                errorCode = ArenaErrorCodes.RunFinalizationFailed;
                failureDetail = "OpenTTD-generated secret files could not be removed from the isolated run directory.";
            }
        }

        ArenaRunState finalState = exitReason switch
        {
            ArenaRunExitReason.Completed => ArenaRunState.Completed,
            ArenaRunExitReason.Cancelled => ArenaRunState.Cancelled,
            _ => ArenaRunState.Failed,
        };
        await journal.TransitionAsync(
            finalState,
            _timeProvider.GetUtcNow(),
            null,
            exitReason,
            errorCode,
            failureDetail,
            CancellationToken.None);

        ArenaRunResult result = new()
        {
            SchemaVersion = ContractVersions.RunResultV1,
            RunId = allocation.RunId,
            CreatedUtc = createdUtc,
            CompletedUtc = _timeProvider.GetUtcNow(),
            FinalState = finalState,
            ExitReason = exitReason,
            ErrorCode = errorCode,
            Components = componentResults,
            Artifacts = layout is null ? [] : BuildArtifactIndex(layout),
        };
        if (layout is not null)
        {
            await WriteResultAsync(layout, result, CancellationToken.None);
        }

        return result;
    }

    private async Task EnsureStartingSaveAsync(
        ArenaLocalConfiguration configuration,
        Phase02RunLayout layout,
        Phase02SmokeOptions options,
        CancellationToken cancellationToken)
    {
        string cachePath = Phase02RunPreparation.GetStartingSaveCachePath(configuration);
        RunPathPolicy runtimePaths = new(configuration.Runtime.Root);
        runtimePaths.CreateDirectory(Path.Combine(ArenaRuntimeLayout.CacheDirectoryName, "phase-02-smoke"));
        runtimePaths.EnsureSafePath(cachePath);
        if (!File.Exists(cachePath))
        {
            Phase02ServerWorkspace templateWorkspace = await Phase02RunPreparation.PrepareServerWorkspaceAsync(
                configuration,
                layout.Allocation.Paths,
                "template-server",
                TemplateServerComponentId,
                cancellationToken);
            await using IManagedArenaProcess templateServer = await StartTemplateServerAsync(
                configuration,
                templateWorkspace,
                cancellationToken);
            try
            {
                await WaitForServerReadinessAsync(configuration, templateServer, options, cancellationToken);
                await SendConsoleCommandAsync(templateServer, OpenTtdConsoleCommand.Pause, cancellationToken);
                string sourceSave = Path.Combine(templateWorkspace.SaveDirectory, TemplateSaveName + ".sav");
                await SaveArtifactAsync(templateServer, sourceSave, TemplateSaveName, options.StartupTimeout, cancellationToken);
                await CopyFileAsync(sourceSave, cachePath, runtimePaths, cancellationToken);
                File.SetAttributes(cachePath, File.GetAttributes(cachePath) | FileAttributes.ReadOnly);
            }
            finally
            {
                await StopServerAsync(templateServer, options.ShutdownTimeout, CancellationToken.None);
            }
        }

        string sourceHash = ComputeFileSha256(cachePath);
        await CopyFileAsync(cachePath, layout.StartingSavePath, layout.Allocation.Paths, cancellationToken);
        if (!string.Equals(sourceHash, ComputeFileSha256(cachePath), StringComparison.Ordinal))
        {
            throw new RunFailureException(
                ArenaRunExitReason.PreparationFailed,
                ArenaErrorCodes.RunArtifactMissing,
                null,
                "The fixed starting save changed while it was being copied.");
        }
    }

    private async Task<IManagedArenaProcess> StartTemplateServerAsync(
        ArenaLocalConfiguration configuration,
        Phase02ServerWorkspace workspace,
        CancellationToken cancellationToken) =>
        await _processFactory.StartAsync(
            new OpenTtdProcessStartRequest(
                workspace.ComponentId,
                configuration.OpenTtd.Executable,
                workspace.WorkingDirectory,
                [
                    "-D",
                    "-d",
                    "script=4,net=4",
                    "-c",
                    workspace.ConfigurationPath,
                    "-g",
                    "-G",
                    Phase02SmokeDefaults.StartingSaveSeed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ],
                workspace.StandardOutputLogPath,
                workspace.StandardErrorLogPath,
                HasWindow: false),
            cancellationToken);

    private async Task<IManagedArenaProcess> StartServerAsync(
        ArenaLocalConfiguration configuration,
        Phase02RunLayout layout,
        string componentId,
        CancellationToken cancellationToken) =>
        await _processFactory.StartAsync(
            new OpenTtdProcessStartRequest(
                componentId,
                configuration.OpenTtd.Executable,
                layout.ServerDirectory,
                [
                    "-D",
                    "-d",
                    "script=4,net=4",
                    "-c",
                    layout.ServerConfigurationPath,
                    "-g",
                    layout.StartingSavePath,
                ],
                layout.Allocation.Paths.Resolve(Path.Combine("component-logs", "server.stdout.log")),
                layout.Allocation.Paths.Resolve(Path.Combine("component-logs", "server.stderr.log")),
                HasWindow: false),
            cancellationToken);

    private async Task<IManagedArenaProcess> StartSpectatorAsync(
        ArenaLocalConfiguration configuration,
        Phase02SpectatorWorkspace workspace,
        CancellationToken cancellationToken) =>
        await _processFactory.StartAsync(
            new OpenTtdProcessStartRequest(
                workspace.Definition.ComponentId,
                configuration.OpenTtd.Executable,
                workspace.WorkingDirectory,
                [
                    "-c",
                    workspace.ConfigurationPath,
                    "-n",
                    $"{configuration.Network.BindAddress}:{ArenaRuntimeLayout.GameServerPort}#255",
                ],
                workspace.StandardOutputLogPath,
                workspace.StandardErrorLogPath,
                HasWindow: true),
            cancellationToken);

    private async Task WaitForServerReadinessAsync(
        ArenaLocalConfiguration configuration,
        IManagedArenaProcess server,
        Phase02SmokeOptions options,
        CancellationToken cancellationToken,
        bool resumeModelProxyFromPausedSave = false)
    {
        bool listening = await _readinessProbe.WaitForPortAsync(
            configuration.Network.BindAddress,
            ArenaRuntimeLayout.GameServerPort,
            options.StartupTimeout,
            cancellationToken);
        if (!listening)
        {
            if (server.HasExited)
            {
                throw ProcessExitedFailure(server, ArenaRunExitReason.ServerExited);
            }

            throw new RunFailureException(
                ArenaRunExitReason.StartupTimedOut,
                ArenaErrorCodes.RunStartupTimedOut,
                server.ComponentId,
                "OpenTTD did not open its loopback game port before the startup timeout.");
        }

        if (resumeModelProxyFromPausedSave)
        {
            await RequireReadinessSignalsAsync(
                server,
                [Phase02SmokeDefaults.GameScriptReadyMarker],
                options.StartupTimeout,
                "ArenaGS did not publish its explicit readiness signal before the startup timeout.",
                cancellationToken);
            await SendConsoleCommandAsync(server, OpenTtdConsoleCommand.Unpause, cancellationToken);
            await RequireReadinessSignalsAsync(
                server,
                [Phase02SmokeDefaults.ModelProxyReadyMarker],
                options.StartupTimeout,
                "ModelProxyAI did not publish its explicit readiness signal before the startup timeout.",
                cancellationToken);
            return;
        }

        await RequireReadinessSignalsAsync(
            server,
            [Phase02SmokeDefaults.GameScriptReadyMarker, Phase02SmokeDefaults.ModelProxyReadyMarker],
            options.StartupTimeout,
            "ArenaGS and ModelProxyAI did not both publish their explicit readiness signals before the startup timeout.",
            cancellationToken);
    }

    private async Task RequireReadinessSignalsAsync(
        IManagedArenaProcess server,
        IReadOnlyCollection<string> expectedSignals,
        TimeSpan timeout,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        bool ready = await WaitForConsoleSignalsAsync(server, expectedSignals, timeout, cancellationToken);
        if (ready)
        {
            return;
        }

        if (server.HasExited)
        {
            throw ProcessExitedFailure(server, ArenaRunExitReason.ServerExited);
        }

        throw new RunFailureException(
            ArenaRunExitReason.GameScriptNotReady,
            ArenaErrorCodes.RunGameScriptNotReady,
            server.ComponentId,
            failureMessage);
    }

    private async Task SendConsoleCommandAsync(
        IManagedArenaProcess server,
        OpenTtdConsoleCommand command,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            if (server.HasExited)
            {
                throw ProcessExitedFailure(server, ArenaRunExitReason.ServerExited);
            }

            try
            {
                await _consoleBridge.SendAsync(server.ProcessId, command, cancellationToken);
                return;
            }
            catch (OpenTtdConsoleControlException exception)
                when (exception.IsTransientAttachmentFailure && attempt < MaximumTransientConsoleAttachmentAttempts)
            {
                if (server.HasExited)
                {
                    throw ProcessExitedFailure(server, ArenaRunExitReason.ServerExited);
                }

                // Windows releases a short-lived bridge process's console attachment
                // asynchronously. Retry only that typed condition, with a bounded
                // attempt count, and only while the supervised server remains alive.
                await Task.Delay(TransientConsoleAttachmentRetryInterval, _timeProvider, cancellationToken);
            }
        }
    }

    private async Task<bool> WaitForConsoleSignalsAsync(
        IManagedArenaProcess server,
        IReadOnlyCollection<string> expectedSignals,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            if (server.HasExited)
            {
                throw ProcessExitedFailure(server, ArenaRunExitReason.ServerExited);
            }

            try
            {
                return await _consoleBridge.WaitForSignalsAsync(
                    server.ProcessId,
                    expectedSignals,
                    timeout,
                    cancellationToken);
            }
            catch (OpenTtdConsoleControlException exception)
                when (exception.IsTransientAttachmentFailure && attempt < MaximumTransientConsoleAttachmentAttempts)
            {
                if (server.HasExited)
                {
                    throw ProcessExitedFailure(server, ArenaRunExitReason.ServerExited);
                }

                await Task.Delay(TransientConsoleAttachmentRetryInterval, _timeProvider, cancellationToken);
            }
        }
    }

    private async Task WaitForSmokeDurationAsync(
        IManagedArenaProcess server,
        IReadOnlyCollection<IManagedArenaProcess> spectators,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = _timeProvider.GetUtcNow().Add(duration);
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (server.HasExited)
            {
                throw ProcessExitedFailure(server, ArenaRunExitReason.ServerExited);
            }

            IManagedArenaProcess? exitedSpectator = spectators.FirstOrDefault(spectator => spectator.HasExited);
            if (exitedSpectator is not null)
            {
                throw ProcessExitedFailure(exitedSpectator, ArenaRunExitReason.SpectatorExited);
            }

            TimeSpan remaining = deadline - _timeProvider.GetUtcNow();
            await Task.Delay(
                remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250),
                _timeProvider,
                cancellationToken);
        }

        if (server.HasExited)
        {
            throw ProcessExitedFailure(server, ArenaRunExitReason.ServerExited);
        }
    }

    private async Task SaveArtifactAsync(
        IManagedArenaProcess server,
        Phase02RunLayout layout,
        string saveName,
        string destination,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string source = Path.Combine(layout.ServerSaveDirectory, saveName + ".sav");
        await SaveArtifactAsync(server, source, saveName, timeout, cancellationToken);
        await CopyFileWhenStableAsync(source, destination, layout.Allocation.Paths, timeout, cancellationToken);
    }

    private async Task SaveArtifactAsync(
        IManagedArenaProcess server,
        string source,
        string saveName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (server.HasExited)
        {
            throw ProcessExitedFailure(server, ArenaRunExitReason.ServerExited);
        }

        await SendConsoleCommandAsync(server, OpenTtdConsoleCommand.Save(saveName), cancellationToken);
        bool available = await WaitForStableFileAsync(source, timeout, cancellationToken);
        if (!available)
        {
            throw new RunFailureException(
                ArenaRunExitReason.FinalizationFailed,
                ArenaErrorCodes.RunArtifactMissing,
                server.ComponentId,
                "OpenTTD did not finish the requested save before the timeout.");
        }
    }

    private static async Task StopSpectatorsAsync(
        IReadOnlyCollection<IManagedArenaProcess> spectators,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        foreach (IManagedArenaProcess spectator in spectators)
        {
            await spectator.RequestGracefulShutdownAsync(cancellationToken);
        }

        foreach (IManagedArenaProcess spectator in spectators)
        {
            if (!await spectator.WaitForExitAsync(timeout, cancellationToken))
            {
                await spectator.ForceTerminateAsync(cancellationToken);
            }
        }
    }

    private async Task StopServerAsync(
        IManagedArenaProcess server,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!server.HasExited)
        {
            await SendConsoleCommandAsync(server, OpenTtdConsoleCommand.Quit, cancellationToken);
        }

        if (!await server.WaitForExitAsync(timeout, cancellationToken))
        {
            await server.ForceTerminateAsync(cancellationToken);
        }
    }

    private static async Task<bool> WaitForStableFileAsync(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        long? priorLength = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileInfo file = new(path);
                if (file.Exists && file.Length > 0)
                {
                    if (priorLength == file.Length)
                    {
                        return true;
                    }

                    priorLength = file.Length;
                }
            }
            catch (IOException)
            {
                priorLength = null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        return false;
    }

    private static async Task CopyFileWhenStableAsync(
        string source,
        string destination,
        RunPathPolicy destinationPaths,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!await WaitForStableFileAsync(source, timeout, cancellationToken))
        {
            throw new RunFailureException(
                ArenaRunExitReason.FinalizationFailed,
                ArenaErrorCodes.RunArtifactMissing,
                null,
                "The OpenTTD save could not be copied because it did not become stable.");
        }

        await CopyFileAsync(source, destination, destinationPaths, cancellationToken);
    }

    private static async Task CopyFileAsync(
        string source,
        string destination,
        RunPathPolicy destinationPaths,
        CancellationToken cancellationToken)
    {
        destinationPaths.EnsureSafePath(destination);
        string? parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException("Run artifact destination has no parent directory.");
        }

        Directory.CreateDirectory(parent);
        await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
    }

    private static async Task WriteResultAsync(
        Phase02RunLayout layout,
        ArenaRunResult result,
        CancellationToken cancellationToken)
    {
        string temporary = layout.Allocation.Paths.Resolve(".run-result.pending.json");
        string content = JsonSerializer.Serialize(result, ResultJsonOptions) + Environment.NewLine;
        await File.WriteAllTextAsync(temporary, content, Utf8WithoutBom, cancellationToken);
        layout.Allocation.Paths.EnsureSafePath(temporary);
        File.Move(temporary, layout.ResultPath, overwrite: true);
    }

    private static List<RunArtifactRecord> BuildArtifactIndex(Phase02RunLayout layout)
    {
        List<RunArtifactRecord> artifacts = [];
        foreach (string path in Directory.EnumerateFiles(layout.Allocation.RunDirectory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(layout.Allocation.RunDirectory, path), StringComparer.Ordinal))
        {
            if (string.Equals(path, layout.ResultPath, StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".pending.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    Path.GetFileName(path),
                    ArenaRuntimeLayout.SecretsConfigurationFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            layout.Allocation.Paths.EnsureSafePath(path);
            string relative = Path.GetRelativePath(layout.Allocation.RunDirectory, path)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            FileInfo file = new(path);
            artifacts.Add(new RunArtifactRecord
            {
                Path = relative,
                Sha256 = ComputeFileSha256(path),
                Bytes = file.Length,
            });
        }

        return artifacts;
    }

    private static void PurgeGeneratedOpenTtdSecrets(Phase02RunLayout layout)
    {
        foreach (string path in Directory.EnumerateFiles(
                     layout.Allocation.RunDirectory,
                     ArenaRuntimeLayout.SecretsConfigurationFileName,
                     SearchOption.AllDirectories))
        {
            layout.Allocation.Paths.EnsureSafePath(path);
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            if (File.Exists(path))
            {
                throw new IOException("OpenTTD-generated secret material could not be removed.");
            }
        }
    }

    private static string ComputeFileSha256(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string SafeFailureDetail(Exception exception)
    {
        string detail = ArtifactTextRedactor.Redact(exception.Message);
        return detail.Length <= 512 ? detail : detail[..512];
    }

    private static List<RunComponentResult> CreateComponentResults(
        IManagedArenaProcess? server,
        IReadOnlyCollection<IManagedArenaProcess> spectators,
        string runId)
    {
        List<RunComponentResult> results = [];
        if (server is not null)
        {
            results.Add(new RunComponentResult
            {
                ComponentId = server.ComponentId,
                ProcessId = server.ProcessId,
                ExitCode = server.ExitCode,
            });
        }

        foreach (IManagedArenaProcess spectator in spectators)
        {
            string stableTitle = Phase02SmokeDefaults.Spectators
                .Single(definition => string.Equals(definition.ComponentId, spectator.ComponentId, StringComparison.Ordinal))
                .StableWindowTitle;
            results.Add(new RunComponentResult
            {
                ComponentId = spectator.ComponentId,
                ProcessId = spectator.ProcessId,
                StableWindowTitle = $"{stableTitle} [{runId}]",
                ExitCode = spectator.ExitCode,
            });
        }

        return results;
    }

    private static void EnsureRuntimeIsReady(ArenaLocalConfiguration configuration)
    {
        RuntimeLayoutInspection inspection = RuntimeLayoutInspector.Inspect(
            configuration.Runtime.Root,
            configuration.Network.BindAddress,
            configuration.OpenTtd.AdminPort);
        if (!inspection.IsValid || !File.Exists(configuration.OpenTtd.Executable))
        {
            throw new RunFailureException(
                ArenaRunExitReason.PreparationFailed,
                ArenaErrorCodes.RuntimeLayoutInvalid,
                null,
                "The isolated OpenTTD runtime is not valid. Run bootstrap and doctor before running the Phase 02 smoke command.");
        }
    }

    private async Task<ArenaRunState> TransitionAsync(
        RunLifecycleJournal journal,
        ArenaRunState nextState,
        string? componentId,
        CancellationToken cancellationToken)
    {
        await journal.TransitionAsync(
            nextState,
            _timeProvider.GetUtcNow(),
            componentId,
            null,
            null,
            null,
            cancellationToken);
        return nextState;
    }

    private static RunFailureException ProcessExitedFailure(
        IManagedArenaProcess process,
        ArenaRunExitReason exitReason) =>
        new(
            exitReason,
            exitReason == ArenaRunExitReason.ServerExited
                ? ArenaErrorCodes.RunServerExited
                : ArenaErrorCodes.RunSpectatorExited,
            process.ComponentId,
            $"OpenTTD component '{process.ComponentId}' exited unexpectedly with code {process.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}.");

    private sealed class RunFailureException : Exception
    {
        public RunFailureException(
            ArenaRunExitReason exitReason,
            string errorCode,
            string? componentId,
            string message)
            : base(message)
        {
            ExitReason = exitReason;
            ErrorCode = errorCode;
            ComponentId = componentId;
        }

        public ArenaRunExitReason ExitReason { get; }

        public string ErrorCode { get; }

        public string? ComponentId { get; }
    }
}
