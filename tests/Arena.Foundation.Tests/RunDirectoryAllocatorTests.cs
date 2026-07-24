using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Storage;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class RunDirectoryAllocatorTests
{
    [Fact]
    public async Task AllocatesDistinctDirectoriesEvenWhenTheClockDoesNotAdvance()
    {
        using TemporaryDirectory directory = new();
        string runsRoot = directory.CreateDirectory("runs");
        RunDirectoryAllocator allocator = new(new SequenceSuffixGenerator("aaaaaaaaaaaa", "bbbbbbbbbbbb", "cccccccccccc", "dddddddddddd"));

        RunDirectoryAllocation first = await allocator.AllocateAsync(runsRoot, "smoke", CancellationToken.None);
        RunDirectoryAllocation second = await allocator.AllocateAsync(runsRoot, "smoke", CancellationToken.None);

        Assert.NotEqual(first.RunId, second.RunId);
        Assert.NotEqual(first.RunDirectory, second.RunDirectory);
        Assert.True(Directory.Exists(first.RunDirectory));
        Assert.True(Directory.Exists(second.RunDirectory));
        Assert.StartsWith(Path.GetFullPath(runsRoot), first.RunDirectory);
        Assert.StartsWith(Path.GetFullPath(runsRoot), second.RunDirectory);
    }

    [Fact]
    public async Task RefusesAnInvalidGeneratedRunIdentifier()
    {
        using TemporaryDirectory directory = new();
        RunDirectoryAllocator allocator = new(new SequenceSuffixGenerator("invalid_suffix"));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await allocator.AllocateAsync(directory.CreateDirectory("runs"), "smoke", CancellationToken.None));

        Assert.StartsWith(ArenaErrorCodes.RunAllocationFailed, exception.Message);
    }

    private sealed class SequenceSuffixGenerator : IRunIdSuffixGenerator
    {
        private readonly Queue<string> _suffixes;

        public SequenceSuffixGenerator(params string[] suffixes)
        {
            _suffixes = new Queue<string>(suffixes);
        }

        public string CreateSuffix() => _suffixes.Count > 0 ? _suffixes.Dequeue() : "eeeeeeeeeeee";
    }
}
