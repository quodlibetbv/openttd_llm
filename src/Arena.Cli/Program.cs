using OpenTtd.ModelArena.Cli;

using CancellationTokenSource cancellation = new();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

Console.CancelKeyPress += cancelHandler;
try
{
    return await ArenaCommandLine.RunAsync(args, cancellation.Token);
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}
