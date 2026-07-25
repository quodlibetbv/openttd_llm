using OpenTtd.ModelArena.Storage;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class RuntimeLayoutBuilderTests
{
    [Fact]
    public async Task CreatesAnIsolatedAndIdempotentRuntimeLayout()
    {
        using TemporaryDirectory directory = new();
        CreatePackageSources(directory);
        string installedOpenTtd = directory.CreateDirectory("installed-openttd");
        File.WriteAllText(Path.Combine(installedOpenTtd, "openttd.exe"), "test executable");
        File.WriteAllText(Path.Combine(installedOpenTtd, "private.cfg"), "profile-only");
        File.WriteAllText(Path.Combine(installedOpenTtd, "secrets.cfg"), "profile-only");
        Directory.CreateDirectory(Path.Combine(installedOpenTtd, "baseset"));
        File.WriteAllText(Path.Combine(installedOpenTtd, "baseset", "placeholder.txt"), "base data");

        RuntimeLayoutRequest request = new(
            directory.Path,
            Path.Combine(directory.Path, ".runtime"),
            installedOpenTtd,
            "127.0.0.1",
            3977);

        RuntimeLayoutResult first = await RuntimeLayoutBuilder.PrepareAsync(request, CancellationToken.None);
        string manifestPath = Path.Combine(directory.Path, ".runtime", "openttd", ArenaRuntimeLayout.ContentManifestFileName);
        string firstManifest = File.ReadAllText(manifestPath);
        RuntimeLayoutResult second = await RuntimeLayoutBuilder.PrepareAsync(request, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        RuntimeLayoutInspection inspection = RuntimeLayoutInspector.Inspect(
            Path.Combine(directory.Path, ".runtime"),
            "127.0.0.1",
            3977);
        Assert.True(inspection.IsValid);
        Assert.Equal(firstManifest, File.ReadAllText(manifestPath));
        Assert.True(File.Exists(Path.Combine(directory.Path, ".runtime", "openttd", "openttd.exe")));
        Assert.True(File.Exists(Path.Combine(directory.Path, ".runtime", "openttd", "game", "ArenaGS", "info.nut")));
        Assert.True(File.Exists(Path.Combine(directory.Path, ".runtime", "openttd", "ai", "ModelProxyAI", "info.nut")));
        Assert.True(File.Exists(Path.Combine(directory.Path, ".runtime", "openttd", "server.cfg")));
        Assert.True(File.Exists(Path.Combine(directory.Path, ".runtime", "openttd", "private.cfg")));
        Assert.False(File.Exists(Path.Combine(directory.Path, ".runtime", "openttd", "secrets.cfg")));

        string configuration = File.ReadAllText(Path.Combine(directory.Path, ".runtime", "openttd", "openttd.cfg"));
        string privateConfiguration = File.ReadAllText(Path.Combine(directory.Path, ".runtime", "openttd", "private.cfg"));
        string serverConfiguration = File.ReadAllText(Path.Combine(directory.Path, ".runtime", "openttd", "server.cfg"));
        Assert.Contains("ini_version = 7", configuration, StringComparison.Ordinal);
        Assert.Contains("autosave_interval = 10", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("server_advertise", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("no_http_content_downloads", configuration, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1 =", privateConfiguration, StringComparison.Ordinal);
        Assert.DoesNotContain("profile-only", privateConfiguration, StringComparison.Ordinal);
        Assert.Contains("server_admin_port = 3977", serverConfiguration, StringComparison.Ordinal);
        Assert.Contains("[game_scripts]", serverConfiguration, StringComparison.Ordinal);
        Assert.Contains("ArenaGS =", serverConfiguration, StringComparison.Ordinal);
        Assert.Contains("ModelProxyAI =", serverConfiguration, StringComparison.Ordinal);
        Assert.Contains("max_no_competitors = 1", serverConfiguration, StringComparison.Ordinal);
        Assert.Contains("competitors_interval = 0", serverConfiguration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetectsAChangedNonLoopbackRuntimeBinding()
    {
        using TemporaryDirectory directory = new();
        CreatePackageSources(directory);
        RuntimeLayoutRequest request = new(
            directory.Path,
            Path.Combine(directory.Path, ".runtime"),
            null,
            "127.0.0.1",
            3977);
        RuntimeLayoutResult result = await RuntimeLayoutBuilder.PrepareAsync(request, CancellationToken.None);
        Assert.True(result.Succeeded);

        File.WriteAllText(
            Path.Combine(directory.Path, ".runtime", "openttd", ArenaRuntimeLayout.PrivateConfigurationFileName),
            "[server_bind_addresses]\n0.0.0.0 =\n");
        RuntimeLayoutInspection inspection = RuntimeLayoutInspector.Inspect(
            Path.Combine(directory.Path, ".runtime"),
            "127.0.0.1",
            3977);

        Assert.False(inspection.IsValid);
        Assert.Contains(ArenaRuntimeLayout.PrivateConfigurationFileName, inspection.MissingOrInvalidItems);
    }

    [Fact]
    public async Task DetectsAChangedPackageThatNoLongerMatchesTheContentManifest()
    {
        using TemporaryDirectory directory = new();
        CreatePackageSources(directory);
        RuntimeLayoutResult result = await RuntimeLayoutBuilder.PrepareAsync(
            new RuntimeLayoutRequest(
                directory.Path,
                Path.Combine(directory.Path, ".runtime"),
                null,
                "127.0.0.1",
                3977),
            CancellationToken.None);
        Assert.True(result.Succeeded);

        File.AppendAllText(
            Path.Combine(directory.Path, ".runtime", "openttd", "game", "ArenaGS", "main.nut"),
            "\n// modified after manifest generation");
        RuntimeLayoutInspection inspection = RuntimeLayoutInspector.Inspect(
            Path.Combine(directory.Path, ".runtime"),
            "127.0.0.1",
            3977);

        Assert.False(inspection.IsValid);
        Assert.Contains("content manifest", inspection.MissingOrInvalidItems);
    }

    [Fact]
    public async Task DetectsUnsupportedPackageMetadataBeforeARunStarts()
    {
        using TemporaryDirectory directory = new();
        CreatePackageSources(directory);
        RuntimeLayoutResult result = await RuntimeLayoutBuilder.PrepareAsync(
            new RuntimeLayoutRequest(
                directory.Path,
                Path.Combine(directory.Path, ".runtime"),
                null,
                "127.0.0.1",
                3977),
            CancellationToken.None);
        Assert.True(result.Succeeded);

        File.WriteAllText(
            Path.Combine(directory.Path, ".runtime", "openttd", "game", "ArenaGS", "info.nut"),
            "class ArenaGSInfo extends GSInfo { function GetShortName() { return \"ARGS\"; } function GetAPIVersion() { return \"1.0\"; } } RegisterGS(ArenaGSInfo()); // ArenaGS");
        RuntimeLayoutInspection inspection = RuntimeLayoutInspector.Inspect(
            Path.Combine(directory.Path, ".runtime"),
            "127.0.0.1",
            3977);

        Assert.False(inspection.IsValid);
        Assert.Contains("ArenaGS metadata", inspection.MissingOrInvalidItems);
    }

    [Fact]
    public async Task RejectsAPackageFileSymbolicLinkEvenWhenItsContentMatchesTheManifest()
    {
        using TemporaryDirectory directory = new();
        CreatePackageSources(directory);
        RuntimeLayoutResult result = await RuntimeLayoutBuilder.PrepareAsync(
            new RuntimeLayoutRequest(
                directory.Path,
                Path.Combine(directory.Path, ".runtime"),
                null,
                "127.0.0.1",
                3977),
            CancellationToken.None);
        Assert.True(result.Succeeded);

        string runtimeMainPath = Path.Combine(
            directory.Path,
            ".runtime",
            "openttd",
            "game",
            "ArenaGS",
            "main.nut");
        string replacementPath = directory.WriteFile("outside-main.nut", File.ReadAllText(runtimeMainPath));
        File.Delete(runtimeMainPath);
        try
        {
            File.CreateSymbolicLink(runtimeMainPath, replacementPath);
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

        RuntimeLayoutInspection inspection = RuntimeLayoutInspector.Inspect(
            Path.Combine(directory.Path, ".runtime"),
            "127.0.0.1",
            3977);

        Assert.False(inspection.IsValid);
        Assert.Contains("content manifest", inspection.MissingOrInvalidItems);
    }

    [Fact]
    public async Task DetectsDuplicateGeneratedServerConfigurationCommands()
    {
        using TemporaryDirectory directory = new();
        CreatePackageSources(directory);
        RuntimeLayoutResult result = await RuntimeLayoutBuilder.PrepareAsync(
            new RuntimeLayoutRequest(
                directory.Path,
                Path.Combine(directory.Path, ".runtime"),
                null,
                "127.0.0.1",
                3977),
            CancellationToken.None);
        Assert.True(result.Succeeded);

        File.AppendAllText(
            Path.Combine(directory.Path, ".runtime", "openttd", ArenaRuntimeLayout.ServerConfigurationFileName),
            "\n[network]\nserver_port = 3979\n");
        RuntimeLayoutInspection inspection = RuntimeLayoutInspector.Inspect(
            Path.Combine(directory.Path, ".runtime"),
            "127.0.0.1",
            3977);

        Assert.False(inspection.IsValid);
        Assert.Contains("generated OpenTTD configuration", inspection.MissingOrInvalidItems);
    }

    [Fact]
    public async Task KeepsTheRuntimeInsideTheRepository()
    {
        using TemporaryDirectory directory = new();
        CreatePackageSources(directory);

        RuntimeLayoutResult result = await RuntimeLayoutBuilder.PrepareAsync(
            new RuntimeLayoutRequest(
                directory.Path,
                Path.Combine(directory.Path, "..", "outside-runtime"),
                null,
                "127.0.0.1",
                3977),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task RejectsANonLoopbackRuntimeBinding()
    {
        using TemporaryDirectory directory = new();
        CreatePackageSources(directory);

        RuntimeLayoutResult result = await RuntimeLayoutBuilder.PrepareAsync(
            new RuntimeLayoutRequest(
                directory.Path,
                Path.Combine(directory.Path, ".runtime"),
                null,
                "192.168.1.10",
                3977),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task RefusesToWriteThroughAnExistingRuntimeSymbolicLink()
    {
        using TemporaryDirectory directory = new();
        CreatePackageSources(directory);
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
        catch (IOException)
        {
            return;
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }

        RuntimeLayoutResult result = await RuntimeLayoutBuilder.PrepareAsync(
            new RuntimeLayoutRequest(
                directory.Path,
                runtimePath,
                null,
                "127.0.0.1",
                3977),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    private static void CreatePackageSources(TemporaryDirectory directory)
    {
        directory.WriteFile("openttd/game/ArenaGS/main.nut", "class ArenaGS {}");
        directory.WriteFile("openttd/game/ArenaGS/info.nut", $"function GetShortName() {{ return \"ARGS\"; }} function GetAPIVersion() {{ return \"{ArenaRuntimeLayout.ArenaGameScriptApiVersion}\"; }} RegisterGS(ArenaGSInfo()); // ArenaGS");
        directory.WriteFile("openttd/ai/ModelProxyAI/main.nut", "class ModelProxyAI {}");
        directory.WriteFile("openttd/ai/ModelProxyAI/info.nut", "function GetShortName() { return \"MPAI\"; } function GetAPIVersion() { return \"1.0\"; } RegisterAI(ModelProxyAIInfo()); // ModelProxyAI");
    }
}
