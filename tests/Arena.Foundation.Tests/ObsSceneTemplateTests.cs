using OpenTtd.ModelArena.Obs;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class ObsSceneTemplateTests
{
    [Fact]
    public async Task GeneratesAndValidatesTheRequiredSceneCollectionTemplate()
    {
        using TemporaryDirectory directory = new();
        string templatePath = Path.Combine(directory.Path, "obs", ObsSceneTemplateGenerator.TemplateFileName);

        ObsSceneTemplateWriteResult write = await ObsSceneTemplateGenerator.WriteAsync(
            directory.Path,
            templatePath,
            CancellationToken.None);
        ObsSceneTemplateValidation validation = ObsSceneTemplateGenerator.ValidateFile(directory.Path, templatePath);

        Assert.True(write.Succeeded);
        Assert.True(validation.IsValid);
        Assert.Empty(validation.MissingRequirements);
    }

    [Fact]
    public void ReportsAMissingTemplateAsInvalid()
    {
        using TemporaryDirectory directory = new();

        ObsSceneTemplateValidation validation = ObsSceneTemplateGenerator.ValidateFile(
            directory.Path,
            Path.Combine(directory.Path, "missing.json"));

        Assert.False(validation.IsValid);
        Assert.Contains("template-file", validation.MissingRequirements);
    }

    [Fact]
    public void RejectsAnOversizedUntrustedTemplate()
    {
        using TemporaryDirectory directory = new();
        string templatePath = directory.WriteFile("obs/template.json", new string('x', 256 * 1024 + 1));

        ObsSceneTemplateValidation validation = ObsSceneTemplateGenerator.ValidateFile(directory.Path, templatePath);

        Assert.False(validation.IsValid);
        Assert.Contains("template-too-large", validation.MissingRequirements);
    }

    [Fact]
    public async Task RefusesToReadOrWriteATemplateThroughAnExistingRuntimeSymbolicLink()
    {
        using TemporaryDirectory directory = new();
        string runtimeRoot = directory.CreateDirectory("runtime");
        string outsideDirectory = directory.CreateDirectory("outside");
        string linkedObsDirectory = Path.Combine(runtimeRoot, "obs");
        try
        {
            Directory.CreateSymbolicLink(linkedObsDirectory, outsideDirectory);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (PlatformNotSupportedException)
        {
            return;
        }

        string templatePath = Path.Combine(linkedObsDirectory, ObsSceneTemplateGenerator.TemplateFileName);
        ObsSceneTemplateWriteResult write = await ObsSceneTemplateGenerator.WriteAsync(
            runtimeRoot,
            templatePath,
            CancellationToken.None);
        ObsSceneTemplateValidation validation = ObsSceneTemplateGenerator.ValidateFile(runtimeRoot, templatePath);

        Assert.False(write.Succeeded);
        Assert.False(validation.IsValid);
        Assert.Contains("template-path", validation.MissingRequirements);
        Assert.False(File.Exists(Path.Combine(outsideDirectory, ObsSceneTemplateGenerator.TemplateFileName)));
    }

    [Fact]
    public async Task RejectsNonLoopbackObsInspectionBeforeOpeningASocket()
    {
        ObsWebSocketInspector inspector = new();
        ObsWebSocketInspectionResult result = await inspector.InspectAsync(
            new ObsWebSocketInspectionRequest(
                "192.168.1.10",
                4455,
                new byte[] { 1 },
                "OpenTTD Model Arena"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("ARENA-OBS-WEBSOCKET-UNAVAILABLE", result.ErrorCode);
    }
}
