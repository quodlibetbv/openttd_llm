using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Orchestrator;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class ArenaConfigurationLoaderTests
{
    [Fact]
    public async Task LoadsAClosedRepositoryLocalConfiguration()
    {
        using TemporaryDirectory directory = new();
        string configurationPath = directory.WriteFile(".config/arena.local.yaml", ValidArenaConfiguration());

        ArenaConfigurationLoadResult result = await ArenaConfigurationLoader.LoadArenaAsync(
            directory.Path,
            configurationPath,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Configuration);
        Assert.StartsWith(directory.Path, result.Configuration.Runtime.Root, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3977, result.Configuration.OpenTtd.AdminPort);
    }

    [Fact]
    public async Task RejectsUnknownRawSecretFields()
    {
        using TemporaryDirectory directory = new();
        string configuration = ValidArenaConfiguration().Replace(
            "  executable: obs64",
            "  executable: obs64\n  password: local-value",
            StringComparison.Ordinal);
        string configurationPath = directory.WriteFile(".config/arena.local.yaml", configuration);

        ArenaConfigurationLoadResult result = await ArenaConfigurationLoader.LoadArenaAsync(
            directory.Path,
            configurationPath,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Code == ArenaErrorCodes.ConfigurationSecretDetected);
    }

    [Fact]
    public async Task RejectsNonLoopbackObsEndpoints()
    {
        using TemporaryDirectory directory = new();
        string configuration = ValidArenaConfiguration().Replace("host: 127.0.0.1", "host: 192.168.1.10", StringComparison.Ordinal);
        string configurationPath = directory.WriteFile(".config/arena.local.yaml", configuration);

        ArenaConfigurationLoadResult result = await ArenaConfigurationLoader.LoadArenaAsync(
            directory.Path,
            configurationPath,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Field == "obs.host");
    }

    [Fact]
    public async Task RejectsAnAdminPortThatCollidesWithTheGeneratedGameServerPort()
    {
        using TemporaryDirectory directory = new();
        string configurationPath = directory.WriteFile(
            ".config/arena.local.yaml",
            ValidArenaConfiguration().Replace("admin_port: 3977", "admin_port: 3979", StringComparison.Ordinal));

        ArenaConfigurationLoadResult result = await ArenaConfigurationLoader.LoadArenaAsync(
            directory.Path,
            configurationPath,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Field == "openttd.admin_port");
    }

    [Fact]
    public async Task RejectsCredentialReferencesOutsideTheManagedTargetAllowlist()
    {
        using TemporaryDirectory directory = new();
        string configurationPath = directory.WriteFile(
            ".config/arena.local.yaml",
            ValidArenaConfiguration().Replace(
                "credman:OpenTTDModelArena/OBS",
                "credman:OpenTTDModelArena/nested/name",
                StringComparison.Ordinal));

        ArenaConfigurationLoadResult result = await ArenaConfigurationLoader.LoadArenaAsync(
            directory.Path,
            configurationPath,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Field == "obs.credential_ref");
    }

    [Fact]
    public async Task RejectsConfigurationThatDisablesSecretRedaction()
    {
        using TemporaryDirectory directory = new();
        string configurationPath = directory.WriteFile(
            ".config/arena.local.yaml",
            ValidArenaConfiguration().Replace("redact_secrets: true", "redact_secrets: false", StringComparison.Ordinal));

        ArenaConfigurationLoadResult result = await ArenaConfigurationLoader.LoadArenaAsync(
            directory.Path,
            configurationPath,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Field == "logging.redact_secrets");
    }

    [Fact]
    public async Task RejectsRuntimePathsThatTraverseAnExistingSymbolicLink()
    {
        using TemporaryDirectory directory = new();
        string runtimePath = Path.Combine(directory.Path, ".runtime");
        string outsidePath = directory.CreateDirectory("outside-runtime");
        try
        {
            Directory.CreateSymbolicLink(runtimePath, outsidePath);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }

        string configurationPath = directory.WriteFile(".config/arena.local.yaml", ValidArenaConfiguration());
        ArenaConfigurationLoadResult result = await ArenaConfigurationLoader.LoadArenaAsync(
            directory.Path,
            configurationPath,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Field == "runtime.root");
    }

    [Fact]
    public async Task RejectsArenaConfigurationReadThroughAnExistingSymbolicLink()
    {
        using TemporaryDirectory directory = new();
        string outsideConfiguration = directory.WriteFile("outside-arena.yaml", ValidArenaConfiguration());
        string configurationDirectory = directory.CreateDirectory(".config");
        string configurationPath = Path.Combine(configurationDirectory, "arena.local.yaml");
        try
        {
            File.CreateSymbolicLink(configurationPath, outsideConfiguration);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }

        ArenaConfigurationLoadResult result = await ArenaConfigurationLoader.LoadArenaAsync(
            directory.Path,
            configurationPath,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Field == "file");
    }

    [Fact]
    public async Task RejectsRawProviderKeyFields()
    {
        using TemporaryDirectory directory = new();
        string providersPath = directory.WriteFile(".config/providers.local.yaml", """
            config_version: 1
            providers:
              deepseek:
                type: deepseek
                api_key: move-to-credential-manager
            """);

        ProviderConfigurationLoadResult result = await ArenaConfigurationLoader.LoadProvidersAsync(
            directory.Path,
            providersPath,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Code == ArenaErrorCodes.ConfigurationSecretDetected);
    }

    [Fact]
    public async Task RejectsYamlSequencesAndProviderUrlsWithUserInfo()
    {
        using TemporaryDirectory directory = new();
        string arenaConfigurationPath = directory.WriteFile(".config/arena.local.yaml", "- not-a-configuration\n");
        string providersPath = directory.WriteFile(".config/providers.local.yaml", """
            config_version: 1
            providers:
              replay:
                type: replay
                base_url: https://user@example.invalid
            """);

        ArenaConfigurationLoadResult arenaResult = await ArenaConfigurationLoader.LoadArenaAsync(
            directory.Path,
            arenaConfigurationPath,
            CancellationToken.None);
        ProviderConfigurationLoadResult providersResult = await ArenaConfigurationLoader.LoadProvidersAsync(
            directory.Path,
            providersPath,
            CancellationToken.None);

        Assert.False(arenaResult.Succeeded);
        Assert.False(providersResult.Succeeded);
        Assert.Contains(providersResult.Errors, error => error.Field == "providers.replay.base_url");
    }

    [Fact]
    public async Task RejectsProviderConfigurationReadThroughAnExistingSymbolicLink()
    {
        using TemporaryDirectory directory = new();
        string outsideConfiguration = directory.WriteFile("outside-providers.yaml", "config_version: 1\nproviders: {}\n");
        string configurationDirectory = directory.CreateDirectory(".config");
        string configurationPath = Path.Combine(configurationDirectory, "providers.local.yaml");
        try
        {
            File.CreateSymbolicLink(configurationPath, outsideConfiguration);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }

        ProviderConfigurationLoadResult result = await ArenaConfigurationLoader.LoadProvidersAsync(
            directory.Path,
            configurationPath,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Field == "file");
    }

    [Fact]
    public async Task RejectsProviderIdentifiersOutsideTheAsciiSchemaAllowlist()
    {
        using TemporaryDirectory directory = new();
        string providersPath = directory.WriteFile(".config/providers.local.yaml", """
            config_version: 1
            providers:
              café:
                type: replay
            """);

        ProviderConfigurationLoadResult result = await ArenaConfigurationLoader.LoadProvidersAsync(
            directory.Path,
            providersPath,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Field == "providers.café");
    }

    internal static string ValidArenaConfiguration() =>
        """
        config_version: 1
        runtime:
          root: .runtime
          runs: .runtime/runs
          recordings: .runtime/recordings
        openttd:
          executable: .runtime/openttd/openttd.exe
          server_config: .runtime/openttd/server.cfg
          spectator_config: .runtime/openttd/spectator.cfg
          admin_port: 3977
        obs:
          host: 127.0.0.1
          port: 4455
          credential_ref: credman:OpenTTDModelArena/OBS
          scene_collection: OpenTTD Model Arena
          executable: obs64
        network:
          bind_address: 127.0.0.1
        logging:
          level: Information
          redact_secrets: true
        doctor:
          minimum_free_disk_gb: 20
        """;
}
