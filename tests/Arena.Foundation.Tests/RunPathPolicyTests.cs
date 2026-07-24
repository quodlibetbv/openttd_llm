using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Storage;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class RunPathPolicyTests
{
    [Fact]
    public void ResolvesAChildArtifactUnderTheRunRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "arena-run-root");
        RunPathPolicy policy = new(root);

        string resolved = policy.Resolve(Path.Combine("component-logs", "orchestrator.ndjson"));

        Assert.StartsWith(Path.GetFullPath(root), resolved);
    }

    [Fact]
    public void RejectsTraversalOutsideTheRunRoot()
    {
        RunPathPolicy policy = new(Path.Combine(Path.GetTempPath(), "arena-run-root"));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => policy.Resolve("../outside.txt"));

        Assert.StartsWith(ArenaErrorCodes.PathOutsideRunRoot, exception.Message);
    }

    [Fact]
    public void RejectsAnExistingSymbolicLinkInsideTheRunRoot()
    {
        using TemporaryDirectory directory = new();
        string runRoot = directory.CreateDirectory("run");
        string outside = directory.CreateDirectory("outside");
        string link = Path.Combine(runRoot, "redirect");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
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

        RunPathPolicy policy = new(runRoot);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => policy.Resolve("redirect/evidence.log"));

        Assert.StartsWith(ArenaErrorCodes.PathOutsideRunRoot, exception.Message);
    }
}
