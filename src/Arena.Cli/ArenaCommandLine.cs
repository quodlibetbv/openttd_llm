using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Obs;
using OpenTtd.ModelArena.Orchestrator;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Cli;

public static class ArenaCommandLine
{
    private static readonly JsonSerializerOptions DoctorJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            if (args.Length == 1 && string.Equals(args[0], "--version", StringComparison.Ordinal))
            {
                Console.WriteLine("ttd-arena 0.6.0 (Phase 04-06 road MVP)");
                return 0;
            }

            if (args.Length > 0 && string.Equals(args[0], "__console-bridge", StringComparison.Ordinal))
            {
                return WindowsDedicatedConsoleBridge.Run(args.Skip(1).ToArray());
            }

            if (args.Length == 0 || IsHelp(args[0]))
            {
                WriteHelp();
                return args.Length == 0 ? 1 : 0;
            }

            string repositoryRoot = RepositoryLocator.FindRoot();
            return args[0] switch
            {
                "bootstrap" => await RunBootstrapAsync(repositoryRoot, args[1..], cancellationToken),
                "doctor" => await RunDoctorAsync(repositoryRoot, args[1..], cancellationToken),
                "smoke" => await RunSmokeAsync(repositoryRoot, args[1..], cancellationToken),
                "bridge-smoke" => await RunBridgeSmokeAsync(repositoryRoot, args[1..], cancellationToken),
                "observation-smoke" => await RunObservationSmokeAsync(repositoryRoot, args[1..], cancellationToken),
                "road-smoke" => await RunRoadSmokeAsync(repositoryRoot, args[1..], cancellationToken),
                "fleet-smoke" => await RunFleetSmokeAsync(repositoryRoot, args[1..], cancellationToken),
                "road-save-load-smoke" => await RunRoadSaveLoadSmokeAsync(repositoryRoot, args[1..], cancellationToken),
                "road-budget-smoke" => await RunRoadBudgetSmokeAsync(repositoryRoot, args[1..], cancellationToken),
                "provider-road-smoke" => await RunProviderRoadSmokeAsync(repositoryRoot, args[1..], cancellationToken),
                "observations" => await RunObservationsAsync(repositoryRoot, args[1..], cancellationToken),
                "credentials" => await RunCredentialsAsync(repositoryRoot, args[1..], cancellationToken),
                "providers" => await RunProvidersAsync(repositoryRoot, args[1..], cancellationToken),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("Operation cancelled.");
            return 130;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(SecretRedactor.Redact(exception.Message));
            return 2;
        }
    }

    private static async Task<int> RunBootstrapAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptions(arguments, ["--config", "--providers-config", "--openttd-source"], out CliOptions options))
        {
            return WriteUsageError(options.ErrorMessage);
        }

        BootstrapResult result = await BootstrapService.RunAsync(
            new BootstrapRequest(
                repositoryRoot,
                ResolveRepositoryOptionPath(repositoryRoot, options, "--config", ".config/arena.local.yaml"),
                ResolveRepositoryOptionPath(repositoryRoot, options, "--providers-config", ".config/providers.local.yaml"),
                ResolveOptionalPath(repositoryRoot, options, "--openttd-source")),
            cancellationToken);
        if (!result.Succeeded)
        {
            WriteError(result.Error);
            return 2;
        }

        Console.WriteLine("Bootstrap completed without modifying the normal OpenTTD or OBS profile.");
        foreach (string item in result.CreatedOrUpdated.Distinct(StringComparer.Ordinal))
        {
            Console.WriteLine($"  ready: {item}");
        }

        foreach (string warning in result.Warnings)
        {
            Console.WriteLine($"  warning: {SecretRedactor.Redact(warning)}");
        }

        Console.WriteLine("Next: set dedicated OBS and AdminPort credentials, configure OBS, then run `doctor`.");
        return 0;
    }

    private static async Task<int> RunDoctorAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptions(arguments, ["--config", "--providers-config", "--json", "--verbose"], out CliOptions options))
        {
            return WriteUsageError(options.ErrorMessage);
        }

        if (options.Positionals.Count > 0)
        {
            return WriteUsageError("doctor does not accept positional arguments.");
        }

        string arenaConfigurationPath = ResolveRepositoryOptionPath(repositoryRoot, options, "--config", ".config/arena.local.yaml");
        string providersConfigurationPath = ResolveRepositoryOptionPath(repositoryRoot, options, "--providers-config", ".config/providers.local.yaml");
        ArenaConfigurationLoadResult arenaConfiguration = await ArenaConfigurationLoader.LoadArenaAsync(
            repositoryRoot,
            arenaConfigurationPath,
            cancellationToken);
        ProviderConfigurationLoadResult providersConfiguration = await ArenaConfigurationLoader.LoadProvidersAsync(
            repositoryRoot,
            providersConfigurationPath,
            cancellationToken);
        DoctorService doctor = CreateDoctorService();
        DoctorReport report = arenaConfiguration.Succeeded && arenaConfiguration.Configuration is not null
            ? await doctor.RunAsync(arenaConfiguration.Configuration, providersConfiguration, cancellationToken)
            : doctor.CreateConfigurationFailureReport(arenaConfiguration.Errors);

        if (options.Flags.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(report, DoctorJsonOptions));
        }
        else
        {
            WriteHumanDoctorReport(report, options.Flags.Contains("--verbose"));
        }

        return report.HasBlockingFailures ? 2 : 0;
    }

    private static async Task<int> RunCredentialsAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count == 0 || IsHelp(arguments[0]))
        {
            Console.WriteLine("Usage: ttd-arena credentials <set|test|list|remove> [argument] [--providers-config <path>]");
            return arguments.Count == 0 ? 1 : 0;
        }

        return arguments[0] switch
        {
            "set" => await SetCredentialAsync(arguments.Skip(1).ToArray(), cancellationToken),
            "remove" => await RemoveCredentialAsync(arguments.Skip(1).ToArray(), cancellationToken),
            "list" => await ListCredentialsAsync(arguments.Skip(1).ToArray(), cancellationToken),
            "test" => await TestCredentialAsync(repositoryRoot, arguments.Skip(1).ToArray(), cancellationToken),
            _ => UnknownCommand($"credentials {arguments[0]}"),
        };
    }

    private static async Task<int> RunProvidersAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count == 0 || IsHelp(arguments[0]))
        {
            Console.WriteLine("Usage: ttd-arena providers <list|test> [provider-id] [--providers-config <path>]");
            return arguments.Count == 0 ? 1 : 0;
        }

        return arguments[0] switch
        {
            "list" => await ListProvidersAsync(repositoryRoot, arguments.Skip(1).ToArray(), cancellationToken),
            "test" => await TestProviderAsync(repositoryRoot, arguments.Skip(1).ToArray(), cancellationToken),
            _ => UnknownCommand($"providers {arguments[0]}"),
        };
    }

    private static async Task<int> RunObservationsAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count == 0 || IsHelp(arguments[0]))
        {
            Console.WriteLine("Usage: ttd-arena observations replay <run-directory|observations.ndjson> [--json]");
            return arguments.Count == 0 ? 1 : 0;
        }

        return arguments[0] switch
        {
            "replay" => await ReplayObservationsAsync(repositoryRoot, arguments.Skip(1).ToArray(), cancellationToken),
            _ => UnknownCommand($"observations {arguments[0]}"),
        };
    }

    private static async Task<int> ReplayObservationsAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptions(arguments, ["--json"], out CliOptions options) || options.Positionals.Count != 1)
        {
            return WriteUsageError("Usage: ttd-arena observations replay <run-directory|observations.ndjson> [--json]");
        }

        string supplied = options.Positionals[0];
        string candidate = Path.IsPathRooted(supplied)
            ? Path.GetFullPath(supplied)
            : Path.GetFullPath(Path.Combine(repositoryRoot, supplied));
        string observationsPath = Directory.Exists(candidate)
            ? Path.Combine(candidate, ObservationArtifactWriter.ObservationsFileName)
            : candidate;
        ObservationReplayResult result = await ObservationReplayReader.ReadAsync(observationsPath, cancellationToken);
        if (options.Flags.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(result, DoctorJsonOptions));
        }
        else if (result.Succeeded)
        {
            ObservationReplayFrame latest = result.Frames[^1];
            Console.WriteLine("Observation replay verified.");
            Console.WriteLine($"  records: {result.Frames.Count}");
            Console.WriteLine($"  run: {latest.RunId}");
            Console.WriteLine($"  latest game date: {latest.GameDate}");
            Console.WriteLine($"  cash / loan: {latest.Cash} / {latest.Loan}");
            Console.WriteLine($"  routes / active projects: {latest.RouteCount} / {latest.ActiveProjectCount}");
            if (latest.TopOpportunityId is not null)
            {
                Console.WriteLine($"  top opportunity: {latest.TopOpportunityId}");
            }

            if (latest.LatestEventCode is not null)
            {
                Console.WriteLine($"  latest event: {latest.LatestEventCode}");
            }
        }
        else
        {
            Console.Error.WriteLine($"{result.ErrorCode}: {SecretRedactor.Redact(result.Detail)}");
        }

        return result.Succeeded ? 0 : 2;
    }

    private static async Task<int> RunSmokeAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptions(
                arguments,
                ["--config", "--duration-seconds", "--startup-timeout-seconds", "--shutdown-timeout-seconds", "--json"],
                out CliOptions options))
        {
            return WriteUsageError(options.ErrorMessage);
        }

        if (options.Positionals.Count > 0)
        {
            return WriteUsageError("smoke does not accept positional arguments.");
        }

        ArenaConfigurationLoadResult configurationResult = await ArenaConfigurationLoader.LoadArenaAsync(
            repositoryRoot,
            ResolveRepositoryOptionPath(repositoryRoot, options, "--config", ".config/arena.local.yaml"),
            cancellationToken);
        if (!configurationResult.Succeeded || configurationResult.Configuration is null)
        {
            foreach (ConfigurationValidationError error in configurationResult.Errors)
            {
                Console.Error.WriteLine($"{error.Code}: {SecretRedactor.Redact(error.Message)}");
            }

            return 2;
        }

        if (!TryGetBoundedSeconds(options, "--duration-seconds", 10, 0, 300, out int runDurationSeconds) ||
            !TryGetBoundedSeconds(options, "--startup-timeout-seconds", 60, 5, 300, out int startupTimeoutSeconds) ||
            !TryGetBoundedSeconds(options, "--shutdown-timeout-seconds", 15, 2, 120, out int shutdownTimeoutSeconds))
        {
            return WriteUsageError("Smoke timeouts must be whole seconds within the documented Phase 02 bounds.");
        }

        Phase02RunService service = new(
            new RunDirectoryAllocator(),
            new SystemArenaProcessFactory(),
            new CliOpenTtdConsoleBridge(),
            new TcpLoopbackReadinessProbe());
        ArenaRunResult result = await service.RunSmokeAsync(
            configurationResult.Configuration,
            new Phase02SmokeOptions(
                TimeSpan.FromSeconds(startupTimeoutSeconds),
                TimeSpan.FromSeconds(runDurationSeconds),
                TimeSpan.FromSeconds(shutdownTimeoutSeconds)),
            cancellationToken);

        if (options.Flags.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(result, DoctorJsonOptions));
        }
        else
        {
            string runDirectory = Path.Combine(configurationResult.Configuration.Runtime.Runs, result.RunId);
            Console.WriteLine($"Phase 02 smoke {result.FinalState.ToString().ToLowerInvariant()} ({result.ExitReason.ToString().ToLowerInvariant()}).");
            Console.WriteLine($"  run: {runDirectory}");
            Console.WriteLine($"  result: {Path.Combine(runDirectory, "run-result.json")}");
            if (result.ErrorCode is not null)
            {
                Console.WriteLine($"  error: {result.ErrorCode}");
            }
        }

        return result.FinalState switch
        {
            ArenaRunState.Completed => 0,
            ArenaRunState.Cancelled => 130,
            _ => 2,
        };
    }

    private static async Task<int> RunBridgeSmokeAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptions(
                arguments,
                ["--config", "--startup-timeout-seconds", "--request-timeout-seconds", "--shutdown-timeout-seconds", "--json"],
                out CliOptions options))
        {
            return WriteUsageError(options.ErrorMessage);
        }

        if (options.Positionals.Count > 0)
        {
            return WriteUsageError("bridge-smoke does not accept positional arguments.");
        }

        ArenaConfigurationLoadResult configurationResult = await ArenaConfigurationLoader.LoadArenaAsync(
            repositoryRoot,
            ResolveRepositoryOptionPath(repositoryRoot, options, "--config", ".config/arena.local.yaml"),
            cancellationToken);
        if (!configurationResult.Succeeded || configurationResult.Configuration is null)
        {
            foreach (ConfigurationValidationError error in configurationResult.Errors)
            {
                Console.Error.WriteLine($"{error.Code}: {SecretRedactor.Redact(error.Message)}");
            }

            return 2;
        }

        if (!TryGetBoundedSeconds(options, "--startup-timeout-seconds", 60, 5, 300, out int startupTimeoutSeconds) ||
            !TryGetBoundedSeconds(options, "--request-timeout-seconds", 20, 8, 60, out int requestTimeoutSeconds) ||
            !TryGetBoundedSeconds(options, "--shutdown-timeout-seconds", 15, 2, 120, out int shutdownTimeoutSeconds))
        {
            return WriteUsageError("Bridge-smoke timeouts must be whole seconds within the documented Phase 03 bounds.");
        }

        Phase03BridgeService service = new(
            new RunDirectoryAllocator(),
            new SystemArenaProcessFactory(),
            new CliOpenTtdConsoleBridge(),
            new TcpLoopbackReadinessProbe(),
            new WindowsCredentialStore());
        Phase03BridgeSmokeResult result = await service.RunAsync(
            configurationResult.Configuration,
            new Phase03BridgeSmokeOptions(
                TimeSpan.FromSeconds(startupTimeoutSeconds),
                TimeSpan.FromSeconds(requestTimeoutSeconds),
                TimeSpan.FromSeconds(shutdownTimeoutSeconds)),
            cancellationToken);

        if (options.Flags.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(result, DoctorJsonOptions));
        }
        else
        {
            string runDirectory = Path.Combine(configurationResult.Configuration.Runtime.Runs, result.RunId);
            Console.WriteLine($"Phase 03 bridge smoke {(result.Succeeded ? "completed" : "failed")}.");
            Console.WriteLine($"  run: {runDirectory}");
            Console.WriteLine($"  result: {Path.Combine(runDirectory, "bridge-result.json")}");
            if (result.ErrorCode is not null)
            {
                Console.WriteLine($"  error: {result.ErrorCode}");
            }
        }

        if (result.Succeeded)
        {
            return 0;
        }

        return string.Equals(result.ErrorCode, ArenaErrorCodes.RunCancelled, StringComparison.Ordinal) ? 130 : 2;
    }

    private static async Task<int> RunObservationSmokeAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptions(
                arguments,
                ["--config", "--startup-timeout-seconds", "--request-timeout-seconds", "--shutdown-timeout-seconds", "--json"],
                out CliOptions options))
        {
            return WriteUsageError(options.ErrorMessage);
        }

        if (options.Positionals.Count > 0)
        {
            return WriteUsageError("observation-smoke does not accept positional arguments.");
        }

        ArenaConfigurationLoadResult configurationResult = await ArenaConfigurationLoader.LoadArenaAsync(
            repositoryRoot,
            ResolveRepositoryOptionPath(repositoryRoot, options, "--config", ".config/arena.local.yaml"),
            cancellationToken);
        if (!configurationResult.Succeeded || configurationResult.Configuration is null)
        {
            foreach (ConfigurationValidationError error in configurationResult.Errors)
            {
                Console.Error.WriteLine($"{error.Code}: {SecretRedactor.Redact(error.Message)}");
            }

            return 2;
        }

        if (!TryGetBoundedSeconds(options, "--startup-timeout-seconds", 60, 5, 300, out int startupTimeoutSeconds) ||
            !TryGetBoundedSeconds(options, "--request-timeout-seconds", 20, 8, 60, out int requestTimeoutSeconds) ||
            !TryGetBoundedSeconds(options, "--shutdown-timeout-seconds", 15, 2, 120, out int shutdownTimeoutSeconds))
        {
            return WriteUsageError("Observation-smoke timeouts must be whole seconds within the documented bridge bounds.");
        }

        Phase03BridgeService service = new(
            new RunDirectoryAllocator(),
            new SystemArenaProcessFactory(),
            new CliOpenTtdConsoleBridge(),
            new TcpLoopbackReadinessProbe(),
            new WindowsCredentialStore());
        Phase03BridgeSmokeResult result = await service.RunAsync(
            configurationResult.Configuration,
            new Phase03BridgeSmokeOptions(
                TimeSpan.FromSeconds(startupTimeoutSeconds),
                TimeSpan.FromSeconds(requestTimeoutSeconds),
                TimeSpan.FromSeconds(shutdownTimeoutSeconds)),
            new Phase04ObservationBridgeExtension(),
            cancellationToken);

        if (options.Flags.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(result, DoctorJsonOptions));
        }
        else
        {
            string runDirectory = Path.Combine(configurationResult.Configuration.Runtime.Runs, result.RunId);
            Console.WriteLine($"Phase 04 observation smoke {(result.Succeeded ? "completed" : "failed")}.");
            Console.WriteLine($"  run: {runDirectory}");
            Console.WriteLine($"  result: {Path.Combine(runDirectory, "bridge-result.json")}");
            Console.WriteLine($"  observations: {Path.Combine(runDirectory, ObservationArtifactWriter.ObservationsFileName)}");
            Console.WriteLine($"  events: {Path.Combine(runDirectory, ObservationArtifactWriter.GameEventsFileName)}");
            if (result.ErrorCode is not null)
            {
                Console.WriteLine($"  error: {result.ErrorCode}");
            }
        }

        return result.Succeeded
            ? 0
            : string.Equals(result.ErrorCode, ArenaErrorCodes.RunCancelled, StringComparison.Ordinal) ? 130 : 2;
    }

    private static Task<int> RunRoadSmokeAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        RunRoadSmokeCoreAsync(repositoryRoot, arguments, false, "road-smoke", "Phase 06 replay road smoke", cancellationToken);

    private static Task<int> RunFleetSmokeAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        RunRoadSmokeCoreAsync(repositoryRoot, arguments, true, "fleet-smoke", "Phase 06 replay road and fleet smoke", cancellationToken);

    private static async Task<int> RunRoadSaveLoadSmokeAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptions(
                arguments,
                ["--stage", "--config", "--startup-timeout-seconds", "--request-timeout-seconds", "--shutdown-timeout-seconds", "--json"],
                out CliOptions options))
        {
            return WriteUsageError(options.ErrorMessage);
        }

        if (options.Positionals.Count > 0 ||
            !options.Values.TryGetValue("--stage", out string? stage) ||
            !RoadProjectCheckpointStages.All.Contains(stage))
        {
            return WriteUsageError(
                "Usage: ttd-arena road-save-load-smoke --stage <proposed|validating|surveying|building_infrastructure|buying_vehicles|configuring_orders|verifying> [--config <path>] [--startup-timeout-seconds <5-300>] [--request-timeout-seconds <8-60>] [--shutdown-timeout-seconds <2-120>] [--json]");
        }

        string checkpointStage = stage;

        ArenaConfigurationLoadResult configurationResult = await ArenaConfigurationLoader.LoadArenaAsync(
            repositoryRoot,
            ResolveRepositoryOptionPath(repositoryRoot, options, "--config", ".config/arena.local.yaml"),
            cancellationToken);
        if (!configurationResult.Succeeded || configurationResult.Configuration is null)
        {
            foreach (ConfigurationValidationError error in configurationResult.Errors)
            {
                Console.Error.WriteLine($"{error.Code}: {SecretRedactor.Redact(error.Message)}");
            }

            return 2;
        }

        if (!TryGetBoundedSeconds(options, "--startup-timeout-seconds", 60, 5, 300, out int startupTimeoutSeconds) ||
            !TryGetBoundedSeconds(options, "--request-timeout-seconds", 20, 8, 60, out int requestTimeoutSeconds) ||
            !TryGetBoundedSeconds(options, "--shutdown-timeout-seconds", 15, 2, 120, out int shutdownTimeoutSeconds))
        {
            return WriteUsageError("road-save-load-smoke timeouts must be whole seconds within the documented bridge bounds.");
        }

        Phase03BridgeService service = new(
            new RunDirectoryAllocator(),
            new SystemArenaProcessFactory(),
            new CliOpenTtdConsoleBridge(),
            new TcpLoopbackReadinessProbe(),
            new WindowsCredentialStore());
        Phase03BridgeSmokeResult result = await service.RunAsync(
            configurationResult.Configuration,
            new Phase03BridgeSmokeOptions(
                TimeSpan.FromSeconds(startupTimeoutSeconds),
                TimeSpan.FromSeconds(requestTimeoutSeconds),
                TimeSpan.FromSeconds(shutdownTimeoutSeconds)),
            new Phase06SaveLoadBridgeExtension(checkpointStage),
            cancellationToken);

        if (options.Flags.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(result, DoctorJsonOptions));
        }
        else
        {
            string runDirectory = Path.Combine(configurationResult.Configuration.Runtime.Runs, result.RunId);
            Console.WriteLine($"Phase 06 {checkpointStage} save/load smoke {(result.Succeeded ? "completed" : "failed")}.");
            Console.WriteLine($"  run: {runDirectory}");
            Console.WriteLine($"  result: {Path.Combine(runDirectory, "bridge-result.json")}");
            Console.WriteLine($"  checkpoint: {Path.Combine(runDirectory, "checkpoints", "phase06-save-load-" + checkpointStage.Replace('_', '-') + ".sav")}");
            if (result.ErrorCode is not null)
            {
                Console.WriteLine($"  error: {result.ErrorCode}");
            }
        }

        return result.Succeeded
            ? 0
            : string.Equals(result.ErrorCode, ArenaErrorCodes.RunCancelled, StringComparison.Ordinal) ? 130 : 2;
    }

    private static async Task<int> RunRoadBudgetSmokeAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptions(
                arguments,
                ["--config", "--startup-timeout-seconds", "--request-timeout-seconds", "--shutdown-timeout-seconds", "--json"],
                out CliOptions options))
        {
            return WriteUsageError(options.ErrorMessage);
        }

        if (options.Positionals.Count > 0)
        {
            return WriteUsageError("road-budget-smoke does not accept positional arguments.");
        }

        ArenaConfigurationLoadResult configurationResult = await ArenaConfigurationLoader.LoadArenaAsync(
            repositoryRoot,
            ResolveRepositoryOptionPath(repositoryRoot, options, "--config", ".config/arena.local.yaml"),
            cancellationToken);
        if (!configurationResult.Succeeded || configurationResult.Configuration is null)
        {
            foreach (ConfigurationValidationError error in configurationResult.Errors)
            {
                Console.Error.WriteLine($"{error.Code}: {SecretRedactor.Redact(error.Message)}");
            }

            return 2;
        }

        if (!TryGetBoundedSeconds(options, "--startup-timeout-seconds", 60, 5, 300, out int startupTimeoutSeconds) ||
            !TryGetBoundedSeconds(options, "--request-timeout-seconds", 20, 8, 60, out int requestTimeoutSeconds) ||
            !TryGetBoundedSeconds(options, "--shutdown-timeout-seconds", 15, 2, 120, out int shutdownTimeoutSeconds))
        {
            return WriteUsageError("road-budget-smoke timeouts must be whole seconds within the documented bridge bounds.");
        }

        Phase03BridgeService service = new(
            new RunDirectoryAllocator(),
            new SystemArenaProcessFactory(),
            new CliOpenTtdConsoleBridge(),
            new TcpLoopbackReadinessProbe(),
            new WindowsCredentialStore());
        Phase03BridgeSmokeResult result = await service.RunAsync(
            configurationResult.Configuration,
            new Phase03BridgeSmokeOptions(
                TimeSpan.FromSeconds(startupTimeoutSeconds),
                TimeSpan.FromSeconds(requestTimeoutSeconds),
                TimeSpan.FromSeconds(shutdownTimeoutSeconds)),
            new Phase06BudgetBoundaryBridgeExtension(),
            cancellationToken);

        if (options.Flags.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(result, DoctorJsonOptions));
        }
        else
        {
            string runDirectory = Path.Combine(configurationResult.Configuration.Runtime.Runs, result.RunId);
            Console.WriteLine($"Phase 06 road budget smoke {(result.Succeeded ? "completed" : "failed")}.");
            Console.WriteLine($"  run: {runDirectory}");
            Console.WriteLine($"  result: {Path.Combine(runDirectory, "bridge-result.json")}");
            if (result.ErrorCode is not null)
            {
                Console.WriteLine($"  error: {result.ErrorCode}");
            }
        }

        return result.Succeeded
            ? 0
            : string.Equals(result.ErrorCode, ArenaErrorCodes.RunCancelled, StringComparison.Ordinal) ? 130 : 2;
    }

    private static async Task<int> RunProviderRoadSmokeAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptions(
                arguments,
                ["--config", "--providers-config", "--startup-timeout-seconds", "--request-timeout-seconds", "--shutdown-timeout-seconds", "--json"],
                out CliOptions options) ||
            options.Positionals.Count != 1)
        {
            return WriteUsageError("Usage: ttd-arena provider-road-smoke <provider-id> [--config <path>] [--providers-config <path>] [--startup-timeout-seconds <5-300>] [--request-timeout-seconds <8-60>] [--shutdown-timeout-seconds <2-120>] [--json]");
        }

        string providerId = options.Positionals[0];
        ArenaConfigurationLoadResult arenaConfiguration = await ArenaConfigurationLoader.LoadArenaAsync(
            repositoryRoot,
            ResolveRepositoryOptionPath(repositoryRoot, options, "--config", ".config/arena.local.yaml"),
            cancellationToken);
        if (!arenaConfiguration.Succeeded || arenaConfiguration.Configuration is null)
        {
            foreach (ConfigurationValidationError error in arenaConfiguration.Errors)
            {
                Console.Error.WriteLine($"{error.Code}: {SecretRedactor.Redact(error.Message)}");
            }

            return 2;
        }

        ProviderConfigurationLoadResult providersConfiguration = await ArenaConfigurationLoader.LoadProvidersAsync(
            repositoryRoot,
            ResolveRepositoryOptionPath(repositoryRoot, options, "--providers-config", ".config/providers.local.yaml"),
            cancellationToken);
        if (!providersConfiguration.Succeeded || providersConfiguration.Configuration is null)
        {
            Console.Error.WriteLine("Provider configuration is invalid. Run doctor --verbose for redacted remediation.");
            return 2;
        }

        if (!providersConfiguration.Configuration.Providers.TryGetValue(providerId, out ProviderLocalConfiguration? providerConfiguration) ||
            !string.Equals(providerConfiguration.Type, "deepseek", StringComparison.Ordinal) ||
            providerConfiguration.CredentialReference is null ||
            string.IsNullOrWhiteSpace(providerConfiguration.Model))
        {
            Console.Error.WriteLine("provider-road-smoke requires a configured DeepSeek provider with model and credential_ref metadata.");
            return 2;
        }

        if (!TryGetBoundedSeconds(options, "--startup-timeout-seconds", 60, 5, 300, out int startupTimeoutSeconds) ||
            !TryGetBoundedSeconds(options, "--request-timeout-seconds", 60, 8, 60, out int requestTimeoutSeconds) ||
            !TryGetBoundedSeconds(options, "--shutdown-timeout-seconds", 15, 2, 120, out int shutdownTimeoutSeconds))
        {
            return WriteUsageError("provider-road-smoke timeouts must be whole seconds within the documented bridge bounds.");
        }

        WindowsCredentialStore credentialStore = new();
        CredentialReadResult credential = await credentialStore.ReadAsync(providerConfiguration.CredentialReference, cancellationToken);
        try
        {
            if (!credential.Succeeded || credential.Secret is null)
            {
                Console.Error.WriteLine($"{credential.ErrorCode}: {SecretRedactor.Redact(credential.UserMessage)}");
                return 2;
            }
        }
        finally
        {
            credential.Secret?.Dispose();
        }

        using HttpClient httpClient = new();
        ProviderCreationResult providerCreation = new ModelProviderFactory(credentialStore, httpClient).Create(providerConfiguration);
        if (!providerCreation.Succeeded || providerCreation.Provider is null)
        {
            WriteError(providerCreation.Error);
            return 2;
        }

        Phase03BridgeService service = new(
            new RunDirectoryAllocator(),
            new SystemArenaProcessFactory(),
            new CliOpenTtdConsoleBridge(),
            new TcpLoopbackReadinessProbe(),
            credentialStore);
        Phase03BridgeSmokeResult result = await service.RunAsync(
            arenaConfiguration.Configuration,
            new Phase03BridgeSmokeOptions(
                TimeSpan.FromSeconds(startupTimeoutSeconds),
                TimeSpan.FromSeconds(requestTimeoutSeconds),
                TimeSpan.FromSeconds(shutdownTimeoutSeconds)),
            new Phase06LiveProviderRoadBridgeExtension(providerCreation.Provider, providerConfiguration.Model),
            cancellationToken);

        if (options.Flags.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(result, DoctorJsonOptions));
        }
        else
        {
            string runDirectory = Path.Combine(arenaConfiguration.Configuration.Runtime.Runs, result.RunId);
            Console.WriteLine($"Phase 05/06 provider road smoke {(result.Succeeded ? "completed" : "failed")}.");
            Console.WriteLine($"  run: {runDirectory}");
            Console.WriteLine($"  result: {Path.Combine(runDirectory, "bridge-result.json")}");
            Console.WriteLine($"  decisions: {Path.Combine(runDirectory, ObservationArtifactWriter.DecisionsFileName)}");
            Console.WriteLine($"  actions: {Path.Combine(runDirectory, ObservationArtifactWriter.ActionsFileName)}");
            Console.WriteLine($"  provider usage: {Path.Combine(runDirectory, ObservationArtifactWriter.ProviderUsageFileName)}");
            if (result.ErrorCode is not null)
            {
                Console.WriteLine($"  error: {result.ErrorCode}");
            }
        }

        return result.Succeeded
            ? 0
            : string.Equals(result.ErrorCode, ArenaErrorCodes.RunCancelled, StringComparison.Ordinal) ? 130 : 2;
    }

    private static async Task<int> RunRoadSmokeCoreAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        bool verifyFleetExpansion,
        string commandName,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptions(
                arguments,
                ["--config", "--startup-timeout-seconds", "--request-timeout-seconds", "--shutdown-timeout-seconds", "--json"],
                out CliOptions options))
        {
            return WriteUsageError(options.ErrorMessage);
        }

        if (options.Positionals.Count > 0)
        {
            return WriteUsageError(commandName + " does not accept positional arguments.");
        }

        ArenaConfigurationLoadResult configurationResult = await ArenaConfigurationLoader.LoadArenaAsync(
            repositoryRoot,
            ResolveRepositoryOptionPath(repositoryRoot, options, "--config", ".config/arena.local.yaml"),
            cancellationToken);
        if (!configurationResult.Succeeded || configurationResult.Configuration is null)
        {
            foreach (ConfigurationValidationError error in configurationResult.Errors)
            {
                Console.Error.WriteLine($"{error.Code}: {SecretRedactor.Redact(error.Message)}");
            }

            return 2;
        }

        if (!TryGetBoundedSeconds(options, "--startup-timeout-seconds", 60, 5, 300, out int startupTimeoutSeconds) ||
            !TryGetBoundedSeconds(options, "--request-timeout-seconds", 20, 8, 60, out int requestTimeoutSeconds) ||
            !TryGetBoundedSeconds(options, "--shutdown-timeout-seconds", 15, 2, 120, out int shutdownTimeoutSeconds))
        {
            return WriteUsageError(commandName + " timeouts must be whole seconds within the documented bridge bounds.");
        }

        Phase03BridgeService service = new(
            new RunDirectoryAllocator(),
            new SystemArenaProcessFactory(),
            new CliOpenTtdConsoleBridge(),
            new TcpLoopbackReadinessProbe(),
            new WindowsCredentialStore());
        Phase03BridgeSmokeResult result = await service.RunAsync(
            configurationResult.Configuration,
            new Phase03BridgeSmokeOptions(
                TimeSpan.FromSeconds(startupTimeoutSeconds),
                TimeSpan.FromSeconds(requestTimeoutSeconds),
                TimeSpan.FromSeconds(shutdownTimeoutSeconds)),
            new Phase06ReplayRoadBridgeExtension(verifyFleetExpansion),
            cancellationToken);

        if (options.Flags.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(result, DoctorJsonOptions));
        }
        else
        {
            string runDirectory = Path.Combine(configurationResult.Configuration.Runtime.Runs, result.RunId);
            Console.WriteLine($"{displayName} {(result.Succeeded ? "completed" : "failed")}.");
            Console.WriteLine($"  run: {runDirectory}");
            Console.WriteLine($"  result: {Path.Combine(runDirectory, "bridge-result.json")}");
            Console.WriteLine($"  decisions: {Path.Combine(runDirectory, ObservationArtifactWriter.DecisionsFileName)}");
            Console.WriteLine($"  actions: {Path.Combine(runDirectory, ObservationArtifactWriter.ActionsFileName)}");
            Console.WriteLine($"  provider usage: {Path.Combine(runDirectory, ObservationArtifactWriter.ProviderUsageFileName)}");
            if (result.ErrorCode is not null)
            {
                Console.WriteLine($"  error: {result.ErrorCode}");
            }
        }

        return result.Succeeded
            ? 0
            : string.Equals(result.ErrorCode, ArenaErrorCodes.RunCancelled, StringComparison.Ordinal) ? 130 : 2;
    }

    private static async Task<int> SetCredentialAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 1 ||
            !TryCreateManagedReference(arguments[0], out CredentialReference? reference) ||
            reference is null)
        {
            return WriteUsageError("Usage: ttd-arena credentials set OpenTTDModelArena/<name>");
        }

        char[]? characters = ReadSecretFromConsole();
        if (characters is null)
        {
            Console.Error.WriteLine("No credential was saved.");
            return 2;
        }

        try
        {
            using SecretMaterial secret = SecretMaterial.FromUtf8(characters);
            CredentialOperationResult result = await new WindowsCredentialStore().SetAsync(reference, secret, cancellationToken);
            WriteCredentialResult(result);
            return result.Succeeded ? 0 : 2;
        }
        finally
        {
            Array.Clear(characters, 0, characters.Length);
        }
    }

    private static async Task<int> RemoveCredentialAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 1 ||
            !TryCreateManagedReference(arguments[0], out CredentialReference? reference) ||
            reference is null)
        {
            return WriteUsageError("Usage: ttd-arena credentials remove OpenTTDModelArena/<name>");
        }

        CredentialOperationResult result = await new WindowsCredentialStore().RemoveAsync(reference, cancellationToken);
        WriteCredentialResult(result);
        return result.Succeeded ? 0 : 2;
    }

    private static async Task<int> ListCredentialsAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 0)
        {
            return WriteUsageError("Usage: ttd-arena credentials list");
        }

        CredentialListResult result = await new WindowsCredentialStore().ListArenaMetadataAsync(cancellationToken);
        if (!result.Succeeded)
        {
            Console.Error.WriteLine($"{result.ErrorCode}: {SecretRedactor.Redact(result.UserMessage)}");
            return 2;
        }

        foreach (CredentialMetadata credential in result.Credentials)
        {
            string lastWritten = credential.LastWrittenUtc is { } value
                ? value.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                : "unknown";
            Console.WriteLine($"{credential.Target} (last written: {lastWritten})");
        }

        if (result.Credentials.Count == 0)
        {
            Console.WriteLine(result.UserMessage);
        }

        return 0;
    }

    private static async Task<int> TestCredentialAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptions(arguments, ["--providers-config"], out CliOptions options) || options.Positionals.Count != 1)
        {
            return WriteUsageError("Usage: ttd-arena credentials test <provider-id|OpenTTDModelArena/name> [--providers-config <path>]");
        }

        string targetOrProviderId = options.Positionals[0];
        if (TryCreateManagedReference(targetOrProviderId, out CredentialReference? directReference) && directReference is not null)
        {
            return await TestCredentialReferenceAsync(
                directReference,
                $"Credential metadata for target '{directReference.Target}' resolves.",
                cancellationToken);
        }

        ProviderConfigurationLoadResult providers = await ArenaConfigurationLoader.LoadProvidersAsync(
            repositoryRoot,
            ResolveRepositoryOptionPath(repositoryRoot, options, "--providers-config", ".config/providers.local.yaml"),
            cancellationToken);
        if (!providers.Succeeded || providers.Configuration is null)
        {
            Console.Error.WriteLine("Provider configuration is invalid. Run doctor --verbose for redacted remediation.");
            return 2;
        }

        if (!providers.Configuration.Providers.TryGetValue(targetOrProviderId, out ProviderLocalConfiguration? provider) ||
            provider.CredentialReference is null)
        {
            Console.Error.WriteLine("The requested provider has no credential_ref in providers.local.yaml.");
            return 2;
        }

        return await TestCredentialReferenceAsync(
            provider.CredentialReference,
            $"Credential metadata for provider '{targetOrProviderId}' resolves. Use `providers test` to validate its adapter configuration without a remote request.",
            cancellationToken);
    }

    private static async Task<int> TestCredentialReferenceAsync(
        CredentialReference reference,
        string successMessage,
        CancellationToken cancellationToken)
    {
        CredentialReadResult result = await new WindowsCredentialStore().ReadAsync(reference, cancellationToken);
        try
        {
            if (!result.Succeeded || result.Secret is null)
            {
                Console.Error.WriteLine($"{result.ErrorCode}: {SecretRedactor.Redact(result.UserMessage)}");
                return 2;
            }

            Console.WriteLine(successMessage);
            return 0;
        }
        finally
        {
            result.Secret?.Dispose();
        }
    }

    private static async Task<int> ListProvidersAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptions(arguments, ["--providers-config"], out CliOptions options) || options.Positionals.Count != 0)
        {
            return WriteUsageError("Usage: ttd-arena providers list [--providers-config <path>]");
        }

        ProviderConfigurationLoadResult providers = await LoadProviderConfigurationAsync(repositoryRoot, options, cancellationToken);
        if (!providers.Succeeded || providers.Configuration is null)
        {
            return 2;
        }

        Console.WriteLine("replay: built-in; adapter=1.0; credential=not-required");
        foreach (ProviderLocalConfiguration provider in providers.Configuration.Providers.Values.OrderBy(provider => provider.Id, StringComparer.Ordinal))
        {
            string model = string.IsNullOrWhiteSpace(provider.Model) ? "not-configured" : "configured";
            string credential = provider.CredentialReference is null ? "not-configured" : "configured";
            string adapter = string.Equals(provider.Type, "deepseek", StringComparison.Ordinal) ? "supported" : "unsupported";
            Console.WriteLine($"{provider.Id}: type={provider.Type}; adapter={adapter}; model={model}; credential={credential}");
        }

        if (providers.Configuration.Providers.Count == 0)
        {
            Console.WriteLine("No local remote-provider configurations were found.");
        }

        return 0;
    }

    private static async Task<int> TestProviderAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptions(arguments, ["--providers-config"], out CliOptions options) || options.Positionals.Count != 1)
        {
            return WriteUsageError("Usage: ttd-arena providers test <provider-id> [--providers-config <path>]");
        }

        string providerId = options.Positionals[0];
        if (string.Equals(providerId, "replay", StringComparison.Ordinal))
        {
            Console.WriteLine("The built-in replay provider is available; no credential or remote request is required.");
            return 0;
        }

        ProviderConfigurationLoadResult providers = await LoadProviderConfigurationAsync(repositoryRoot, options, cancellationToken);
        if (!providers.Succeeded || providers.Configuration is null)
        {
            return 2;
        }

        if (!providers.Configuration.Providers.TryGetValue(providerId, out ProviderLocalConfiguration? provider))
        {
            Console.Error.WriteLine("The requested provider is not present in providers.local.yaml.");
            return 2;
        }

        WindowsCredentialStore credentialStore = new();
        using HttpClient client = new();
        ProviderCreationResult creation = new ModelProviderFactory(credentialStore, client).Create(provider);
        if (!creation.Succeeded)
        {
            WriteError(creation.Error);
            return 2;
        }

        if (provider.CredentialReference is null)
        {
            Console.Error.WriteLine("The requested provider has no credential_ref in providers.local.yaml.");
            return 2;
        }

        CredentialReadResult credential = await credentialStore.ReadAsync(provider.CredentialReference, cancellationToken);
        try
        {
            if (!credential.Succeeded || credential.Secret is null)
            {
                Console.Error.WriteLine($"{credential.ErrorCode}: {SecretRedactor.Redact(credential.UserMessage)}");
                return 2;
            }

            Console.WriteLine($"Provider '{providerId}' configuration and credential reference resolve. No remote provider request was made.");
            return 0;
        }
        finally
        {
            credential.Secret?.Dispose();
        }
    }

    private static async Task<ProviderConfigurationLoadResult> LoadProviderConfigurationAsync(
        string repositoryRoot,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        ProviderConfigurationLoadResult providers = await ArenaConfigurationLoader.LoadProvidersAsync(
            repositoryRoot,
            ResolveRepositoryOptionPath(repositoryRoot, options, "--providers-config", ".config/providers.local.yaml"),
            cancellationToken);
        if (!providers.Succeeded || providers.Configuration is null)
        {
            Console.Error.WriteLine("Provider configuration is invalid. Run doctor --verbose for redacted remediation.");
        }

        return providers;
    }

    private static DoctorService CreateDoctorService() =>
        new(
            new DoctorSystemProbe(),
            new WindowsCredentialStore(),
            new ObsWebSocketInspector(),
            new SystemDoctorClock());

    private static void WriteHumanDoctorReport(DoctorReport report, bool verbose)
    {
        foreach (DoctorCheckResult check in report.Checks)
        {
            string status = check.Status switch
            {
                DoctorCheckStatus.Pass => "PASS",
                DoctorCheckStatus.Warning => "WARN",
                DoctorCheckStatus.BlockingFailure => "BLOCK",
                _ => "UNKNOWN",
            };
            Console.WriteLine($"[{status}] {check.Id}: {SecretRedactor.Redact(check.Summary)}");
            if (check.Status != DoctorCheckStatus.Pass || verbose)
            {
                Console.WriteLine($"       remediation: {SecretRedactor.Redact(check.Remediation)}");
            }

            if (verbose && check.Detail is not null)
            {
                Console.WriteLine($"       detail: {SecretRedactor.Redact(check.Detail)}");
            }
        }

        Console.WriteLine(report.HasBlockingFailures
            ? "Doctor found blocking failures. Resolve each BLOCK item before starting a future run phase."
            : "Doctor found no blocking failures.");
    }

    private static bool TryParseOptions(
        IReadOnlyList<string> arguments,
        IReadOnlyCollection<string> allowedOptions,
        out CliOptions options)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        HashSet<string> flags = new(StringComparer.Ordinal);
        List<string> positionals = [];
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(argument);
                continue;
            }

            if (!allowedOptions.Contains(argument, StringComparer.Ordinal))
            {
                options = CliOptions.Error($"Unsupported option: {argument}");
                return false;
            }

            if (argument is "--json" or "--verbose")
            {
                if (!flags.Add(argument))
                {
                    options = CliOptions.Error($"Option may only be supplied once: {argument}");
                    return false;
                }

                continue;
            }

            if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                !values.TryAdd(argument, arguments[++index]))
            {
                options = CliOptions.Error($"Option requires exactly one value: {argument}");
                return false;
            }
        }

        options = new CliOptions(values, flags, positionals, null);
        return true;
    }

    private static string ResolveOptionPath(
        string repositoryRoot,
        CliOptions options,
        string optionName,
        string defaultRelativePath)
    {
        string path = options.Values.TryGetValue(optionName, out string? supplied)
            ? supplied
            : defaultRelativePath;
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(repositoryRoot, path));
    }

    private static string ResolveRepositoryOptionPath(
        string repositoryRoot,
        CliOptions options,
        string optionName,
        string defaultRelativePath)
    {
        string path = ResolveOptionPath(repositoryRoot, options, optionName, defaultRelativePath);
        string normalizedRoot = Path.GetFullPath(repositoryRoot);
        string rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{optionName} must resolve inside this repository.");
        }

        return path;
    }

    private static string? ResolveOptionalPath(string repositoryRoot, CliOptions options, string optionName) =>
        options.Values.TryGetValue(optionName, out string? supplied)
            ? ResolveOptionPath(repositoryRoot, options, optionName, supplied)
            : null;

    private static bool TryCreateManagedReference(string target, out CredentialReference? reference)
    {
        reference = null;
        if (!CredentialReference.IsArenaManagedTarget(target))
        {
            return false;
        }

        return CredentialReference.TryParse(CredentialReference.SchemePrefix + target, out reference);
    }

    private static bool TryGetBoundedSeconds(
        CliOptions options,
        string optionName,
        int defaultValue,
        int minimum,
        int maximum,
        out int value)
    {
        value = defaultValue;
        if (!options.Values.TryGetValue(optionName, out string? supplied))
        {
            return true;
        }

        return int.TryParse(
                   supplied,
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out value) &&
            value >= minimum &&
            value <= maximum;
    }

    private static char[]? ReadSecretFromConsole()
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Credential entry requires an interactive console and is never accepted from standard input.");
            return null;
        }

        Console.Write("Enter credential value (input is hidden): ");
        List<char> characters = [];
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(true);
            if (key.Key is ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key is ConsoleKey.Escape ||
                (key.Key is ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0))
            {
                Console.WriteLine();
                char[] cancelled = characters.ToArray();
                Array.Clear(cancelled, 0, cancelled.Length);
                return null;
            }

            if (key.Key is ConsoleKey.Backspace)
            {
                if (characters.Count > 0)
                {
                    characters[^1] = '\0';
                    characters.RemoveAt(characters.Count - 1);
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                characters.Add(key.KeyChar);
            }
        }

        char[] result = characters.ToArray();
        for (int index = 0; index < characters.Count; index++)
        {
            characters[index] = '\0';
        }

        return result;
    }

    private static void WriteCredentialResult(CredentialOperationResult result)
    {
        TextWriter output = result.Succeeded ? Console.Out : Console.Error;
        if (result.ErrorCode is not null)
        {
            output.WriteLine($"{result.ErrorCode}: {SecretRedactor.Redact(result.UserMessage)}");
        }
        else
        {
            output.WriteLine(SecretRedactor.Redact(result.UserMessage));
        }
    }

    private static void WriteError(ArenaError? error)
    {
        if (error is null)
        {
            Console.Error.WriteLine("Bootstrap failed without a classified error.");
            return;
        }

        Console.Error.WriteLine($"{error.Code}: {SecretRedactor.Redact(error.UserMessage)}");
    }

    private static bool IsHelp(string value) => value is "--help" or "-h" or "help";

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        WriteHelp();
        return 1;
    }

    private static int WriteUsageError(string? message)
    {
        Console.Error.WriteLine(message ?? "Invalid command arguments.");
        return 1;
    }

    private static void WriteHelp()
    {
        Console.WriteLine("OpenTTD Model Arena Phase 04-06 commands:");
        Console.WriteLine("  ttd-arena bootstrap [--config <path>] [--providers-config <path>] [--openttd-source <directory>]");
        Console.WriteLine("  ttd-arena doctor [--config <path>] [--providers-config <path>] [--json] [--verbose]");
        Console.WriteLine("  ttd-arena smoke [--config <path>] [--duration-seconds <0-300>] [--startup-timeout-seconds <5-300>] [--shutdown-timeout-seconds <2-120>] [--json]");
        Console.WriteLine("  ttd-arena bridge-smoke [--config <path>] [--startup-timeout-seconds <5-300>] [--request-timeout-seconds <8-60>] [--shutdown-timeout-seconds <2-120>] [--json]");
        Console.WriteLine("  ttd-arena observation-smoke [--config <path>] [--startup-timeout-seconds <5-300>] [--request-timeout-seconds <8-60>] [--shutdown-timeout-seconds <2-120>] [--json]");
        Console.WriteLine("  ttd-arena road-smoke [--config <path>] [--startup-timeout-seconds <5-300>] [--request-timeout-seconds <8-60>] [--shutdown-timeout-seconds <2-120>] [--json]");
        Console.WriteLine("  ttd-arena fleet-smoke [--config <path>] [--startup-timeout-seconds <5-300>] [--request-timeout-seconds <8-60>] [--shutdown-timeout-seconds <2-120>] [--json]");
        Console.WriteLine("  ttd-arena road-save-load-smoke --stage <proposed|validating|surveying|building_infrastructure|buying_vehicles|configuring_orders|verifying> [--config <path>] [--startup-timeout-seconds <5-300>] [--request-timeout-seconds <8-60>] [--shutdown-timeout-seconds <2-120>] [--json]");
        Console.WriteLine("  ttd-arena road-budget-smoke [--config <path>] [--startup-timeout-seconds <5-300>] [--request-timeout-seconds <8-60>] [--shutdown-timeout-seconds <2-120>] [--json]");
        Console.WriteLine("  ttd-arena provider-road-smoke <provider-id> [--config <path>] [--providers-config <path>] [--startup-timeout-seconds <5-300>] [--request-timeout-seconds <8-60>] [--shutdown-timeout-seconds <2-120>] [--json]");
        Console.WriteLine("  ttd-arena observations replay <run-directory|observations.ndjson> [--json]");
        Console.WriteLine("  ttd-arena providers list [--providers-config <path>]");
        Console.WriteLine("  ttd-arena providers test <provider-id> [--providers-config <path>]");
        Console.WriteLine("  ttd-arena credentials set OpenTTDModelArena/<name>");
        Console.WriteLine("  ttd-arena credentials test <provider-id|OpenTTDModelArena/name> [--providers-config <path>]");
        Console.WriteLine("  ttd-arena credentials list");
        Console.WriteLine("  ttd-arena credentials remove OpenTTDModelArena/<name>");
    }

    private sealed record CliOptions(
        IReadOnlyDictionary<string, string> Values,
        IReadOnlySet<string> Flags,
        IReadOnlyList<string> Positionals,
        string? ErrorMessage)
    {
        public static CliOptions Error(string errorMessage) =>
            new(new Dictionary<string, string>(), new HashSet<string>(), [], errorMessage);
    }

    private static class RepositoryLocator
    {
        public static string FindRoot()
        {
            DirectoryInfo? current = new(Directory.GetCurrentDirectory());
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "OpenTTD.ModelArena.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Could not find OpenTTD.ModelArena.sln. Run the command from this repository or use the repository script wrapper.");
        }
    }
}
