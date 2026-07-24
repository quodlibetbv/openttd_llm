using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Storage;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class RunLifecycleJournalTests
{
    [Fact]
    public async Task PersistsAndClassifiesAnInterruptedStartup()
    {
        using TemporaryDirectory directory = new();
        string runDirectory = directory.CreateDirectory("run");
        RunPathPolicy paths = new(runDirectory);
        using RunLifecycleJournal journal = new("smoke-20260723t190000000z-a1b2c3d4e5f6", paths);
        DateTimeOffset timestamp = new(2026, 7, 23, 19, 0, 0, TimeSpan.Zero);

        await journal.InitializeAsync(timestamp, CancellationToken.None);
        await journal.TransitionAsync(ArenaRunState.Preparing, timestamp.AddSeconds(1), null, null, null, null, CancellationToken.None);
        await journal.TransitionAsync(ArenaRunState.StartingServer, timestamp.AddSeconds(2), "server", null, null, null, CancellationToken.None);

        RunLifecycleEvent? latest = RunLifecycleJournal.ReadLatest(runDirectory);
        InterruptedRunClassification? classification = RunLifecycleJournal.ClassifyInterrupted(runDirectory);

        Assert.Equal(ArenaRunState.StartingServer, latest?.State);
        Assert.NotNull(classification);
        Assert.Equal(ArenaRunExitReason.StartupTimedOut, classification!.SuggestedExitReason);
        Assert.Contains("starting_server", File.ReadAllText(Path.Combine(runDirectory, RunLifecycleJournal.FileName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsAStateTransitionThatSkipsTheLifecycle()
    {
        using TemporaryDirectory directory = new();
        using RunLifecycleJournal journal = new(
            "smoke-20260723t190000000z-a1b2c3d4e5f6",
            new RunPathPolicy(directory.CreateDirectory("run")));
        DateTimeOffset timestamp = new(2026, 7, 23, 19, 0, 0, TimeSpan.Zero);
        await journal.InitializeAsync(timestamp, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await journal.TransitionAsync(ArenaRunState.Running, timestamp, null, null, null, null, CancellationToken.None));
    }
}
