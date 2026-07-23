using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Orchestrator;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class BootstrapServiceTests
{
    [Fact]
    public async Task IsIdempotentAndDoesNotOverwriteAnExistingLocalConfiguration()
    {
        using TemporaryDirectory directory = new();
        directory.WriteFile(".config/arena.example.yaml", ArenaConfigurationLoaderTests.ValidArenaConfiguration());
        directory.WriteFile(".config/providers.example.yaml", "config_version: 1\nproviders: {}\n");
        directory.WriteFile(".config/arena.local.yaml", ArenaConfigurationLoaderTests.ValidArenaConfiguration().Replace(
            "level: Information",
            "level: Debug",
            StringComparison.Ordinal));
        directory.WriteFile(".config/providers.local.yaml", "config_version: 1\nproviders: {}\n");
        directory.WriteFile("openttd/game/ArenaGS/main.nut", "class ArenaGS {}");
        directory.WriteFile("openttd/game/ArenaGS/info.nut", "ArenaGS RegisterGS");
        directory.WriteFile("openttd/ai/ModelProxyAI/main.nut", "class ModelProxyAI {}");
        directory.WriteFile("openttd/ai/ModelProxyAI/info.nut", "ModelProxyAI RegisterAI");
        string localConfigurationPath = Path.Combine(directory.Path, ".config", "arena.local.yaml");
        string expectedLocalConfiguration = File.ReadAllText(localConfigurationPath);

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
        directory.WriteFile("openttd/game/ArenaGS/info.nut", "ArenaGS RegisterGS");
        directory.WriteFile("openttd/ai/ModelProxyAI/main.nut", "class ModelProxyAI {}");
        directory.WriteFile("openttd/ai/ModelProxyAI/info.nut", "ModelProxyAI RegisterAI");
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
