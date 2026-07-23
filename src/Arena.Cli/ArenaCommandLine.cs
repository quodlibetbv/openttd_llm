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
                Console.WriteLine("ttd-arena 0.1.0 (Phase 01 setup and doctor)");
                return 0;
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
                "credentials" => await RunCredentialsAsync(repositoryRoot, args[1..], cancellationToken),
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

        Console.WriteLine("Next: set the dedicated OBS password with `credentials set OpenTTDModelArena/OBS`, configure OBS, then run `doctor`.");
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
            $"Credential metadata for provider '{targetOrProviderId}' resolves. Remote provider calls begin in Phase 05.",
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
        Console.WriteLine("OpenTTD Model Arena Phase 01 setup commands:");
        Console.WriteLine("  ttd-arena bootstrap [--config <path>] [--providers-config <path>] [--openttd-source <directory>]");
        Console.WriteLine("  ttd-arena doctor [--config <path>] [--providers-config <path>] [--json] [--verbose]");
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
