using System.Security.Cryptography;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Storage;

public interface IRunIdSuffixGenerator
{
    string CreateSuffix();
}

public sealed class CryptographicRunIdSuffixGenerator : IRunIdSuffixGenerator
{
    public string CreateSuffix() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
}

public sealed record RunDirectoryAllocation(
    string RunId,
    string RunDirectory,
    RunPathPolicy Paths);

/// <summary>
/// Allocates an isolated run directory using a generated identifier. The caller
/// never supplies a directory name, which keeps artifacts inside runtime.runs.
/// </summary>
public sealed class RunDirectoryAllocator
{
    private const int MaximumAllocationAttempts = 32;
    private readonly IRunIdSuffixGenerator _suffixGenerator;
    private readonly TimeProvider _timeProvider;

    public RunDirectoryAllocator(
        IRunIdSuffixGenerator? suffixGenerator = null,
        TimeProvider? timeProvider = null)
    {
        _suffixGenerator = suffixGenerator ?? new CryptographicRunIdSuffixGenerator();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<RunDirectoryAllocation> AllocateAsync(
        string runsRoot,
        string runKind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValidRunKind(runKind))
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.RunAllocationFailed}: run kind is invalid.");
        }

        RunPathPolicy rootPolicy = new(runsRoot);
        Directory.CreateDirectory(Path.GetFullPath(runsRoot));
        rootPolicy.EnsureSafePath(Path.GetFullPath(runsRoot));

        for (int attempt = 0; attempt < MaximumAllocationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string runId = CreateRunId(runKind, _timeProvider.GetUtcNow(), _suffixGenerator.CreateSuffix());
            string destination = rootPolicy.Resolve(runId);
            string stagingName = $".{runId}.allocating-{_suffixGenerator.CreateSuffix()}";
            string staging = rootPolicy.Resolve(stagingName);

            try
            {
                Directory.CreateDirectory(staging);
                rootPolicy.EnsureSafePath(staging);
                Directory.Move(staging, destination);
                RunPathPolicy runPolicy = new(destination);
                runPolicy.EnsureSafePath(destination);
                return Task.FromResult(new RunDirectoryAllocation(runId, destination, runPolicy));
            }
            catch (IOException)
            {
                DeleteStagingDirectory(rootPolicy, staging);
            }
        }

        throw new InvalidOperationException(
            $"{ArenaErrorCodes.RunAllocationFailed}: could not reserve a unique run directory after {MaximumAllocationAttempts} attempts.");
    }

    internal static string CreateRunId(string runKind, DateTimeOffset createdUtc, string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        string result = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{runKind}-{createdUtc:yyyyMMdd't'HHmmssfff'z'}-{suffix.ToLowerInvariant()}");
        if (!IsValidRunId(result))
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.RunAllocationFailed}: generated run identifier is invalid.");
        }

        return result;
    }

    private static void DeleteStagingDirectory(RunPathPolicy rootPolicy, string staging)
    {
        if (!Directory.Exists(staging))
        {
            return;
        }

        rootPolicy.EnsureSafePath(staging);
        Directory.Delete(staging, true);
    }

    private static bool IsValidRunKind(string runKind) =>
        runKind.Length is >= 3 and <= 24 &&
        runKind.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static bool IsValidRunId(string runId) =>
        runId.Length is >= 3 and <= 96 &&
        runId[0] is >= 'a' and <= 'z' &&
        runId.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
