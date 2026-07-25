using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Orchestrator;
using OpenTtd.ModelArena.Storage;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class BootstrapServiceTests
{
    [Fact]
    public async Task IsIdempotentAndDoesNotOverwriteAnExistingLocalConfiguration()
    {
        using TemporaryDirectory directory = new();
        string legacyArenaConfiguration = ArenaConfigurationLoaderTests.ValidArenaConfiguration().Replace(
            "  admin_credential_ref: credman:OpenTTDModelArena/AdminPort\n",
            string.Empty,
            StringComparison.Ordinal);
        directory.WriteFile(".config/arena.example.yaml", legacyArenaConfiguration);
        directory.WriteFile(".config/providers.example.yaml", "config_version: 1\nproviders: {}\n");
        directory.WriteFile(".config/arena.local.yaml", legacyArenaConfiguration.Replace(
            "level: Information",
            "level: Debug",
            StringComparison.Ordinal));
        directory.WriteFile(".config/providers.local.yaml", "config_version: 1\nproviders: {}\n");
        directory.WriteFile("openttd/game/ArenaGS/main.nut", "class ArenaGS {}");
        directory.WriteFile("openttd/game/ArenaGS/info.nut", $"ArenaGS function GetShortName() {{ return \"ARGS\"; }} function GetAPIVersion() {{ return \"{ArenaRuntimeLayout.ArenaGameScriptApiVersion}\"; }} RegisterGS");
        directory.WriteFile("openttd/ai/ModelProxyAI/main.nut", "class ModelProxyAI {}");
        directory.WriteFile("openttd/ai/ModelProxyAI/info.nut", "ModelProxyAI function GetShortName() { return \"MPAI\"; } function GetAPIVersion() { return \"1.0\"; } RegisterAI");
        string localConfigurationPath = Path.Combine(directory.Path, ".config", "arena.local.yaml");
        string expectedLocalConfiguration = File.ReadAllText(localConfigurationPath).Replace(
            "admin_port: 3977",
            "admin_port: 3977\n  admin_credential_ref: credman:OpenTTDModelArena/AdminPort",
            StringComparison.Ordinal);

        BootstrapRequest request = new(
            directory.Path,
            localConfigurationPath,
            Path.Combine(directory.Path, ".config", "providers.local.yaml"),
            null);
        BootstrapResult first = await BootstrapService.RunAsync(request, CancellationToken.None);
        BootstrapResult second = await BootstrapService.RunAsync(request, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(expectedLocalConfiguration, File.ReadAllText(localConfigurationPath));
        Assert.Contains("Phase 03 AdminPort credential reference", first.CreatedOrUpdated);
        string generatedConfiguration = File.ReadAllText(Path.Combine(directory.Path, ".runtime", "openttd", "openttd.cfg"));
        Assert.Contains("resolution = 2560,1440", generatedConfiguration, StringComparison.Ordinal);
        Assert.Contains("gui_scale = 100", generatedConfiguration, StringComparison.Ordinal);
        Assert.Contains("autosave_interval = 10", generatedConfiguration, StringComparison.Ordinal);
        Assert.Contains("server_admin_port = 3977", generatedConfiguration, StringComparison.Ordinal);
        Assert.DoesNotContain("no_http_content_downloads", generatedConfiguration, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(directory.Path, ".runtime", "openttd", "private.cfg")));
        Assert.False(File.Exists(Path.Combine(directory.Path, ".runtime", "openttd", "secrets.cfg")));
        Assert.True(Directory.Exists(Path.Combine(directory.Path, ".runtime", "runs")));
        Assert.True(Directory.Exists(Path.Combine(directory.Path, ".runtime", "recordings")));
    }

    [Fact]
    public async Task RefusesToWriteLocalConfigurationThroughAnExistingSymbolicLink()
    {
        using TemporaryDirectory directory = new();
        directory.WriteFile(".config/arena.example.yaml", ArenaConfigurationLoaderTests.ValidArenaConfiguration());
        directory.WriteFile(".config/providers.example.yaml", "config_version: 1\nproviders: {}\n");
        directory.WriteFile("openttd/game/ArenaGS/main.nut", "class ArenaGS {}");
        directory.WriteFile("openttd/game/ArenaGS/info.nut", $"ArenaGS function GetShortName() {{ return \"ARGS\"; }} function GetAPIVersion() {{ return \"{ArenaRuntimeLayout.ArenaGameScriptApiVersion}\"; }} RegisterGS");
        directory.WriteFile("openttd/ai/ModelProxyAI/main.nut", "class ModelProxyAI {}");
        directory.WriteFile("openttd/ai/ModelProxyAI/info.nut", "ModelProxyAI function GetShortName() { return \"MPAI\"; } function GetAPIVersion() { return \"1.0\"; } RegisterAI");
        string outsideDirectory = directory.CreateDirectory("outside-config");
        string linkedDirectory = Path.Combine(directory.Path, ".config", "linked");
        try
        {
            Directory.CreateSymbolicLink(linkedDirectory, outsideDirectory);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }

        BootstrapResult result = await BootstrapService.RunAsync(
            new BootstrapRequest(
                directory.Path,
                Path.Combine(linkedDirectory, "arena.local.yaml"),
                Path.Combine(directory.Path, ".config", "providers.local.yaml"),
                null),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ArenaErrorCodes.ConfigurationInvalid, result.Error?.Code);
        Assert.False(File.Exists(Path.Combine(outsideDirectory, "arena.local.yaml")));
    }
}
