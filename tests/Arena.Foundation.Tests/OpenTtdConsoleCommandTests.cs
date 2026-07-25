using OpenTtd.ModelArena.Orchestrator;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class OpenTtdConsoleCommandTests
{
    [Fact]
    public void CreatesValidatedSaveAndLoadCommandsForTheSameRunLocalCheckpoint()
    {
        OpenTtdConsoleCommand save = OpenTtdConsoleCommand.Save("phase06-save-load-verifying");
        OpenTtdConsoleCommand load = OpenTtdConsoleCommand.Load("phase06-save-load-verifying");

        Assert.Equal(OpenTtdConsoleOperation.Save, save.Operation);
        Assert.Equal(OpenTtdConsoleOperation.Load, load.Operation);
        Assert.Equal(save.SaveName, load.SaveName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("with space")]
    [InlineData("with_underscore")]
    [InlineData("Uppercase")]
    public void RejectsUnsafeSaveAndLoadNames(string name)
    {
        Assert.Throws<ArgumentException>(() => OpenTtdConsoleCommand.Save(name));
        Assert.Throws<ArgumentException>(() => OpenTtdConsoleCommand.Load(name));
    }
}
