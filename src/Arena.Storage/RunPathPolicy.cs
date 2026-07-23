using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Storage;

public sealed class RunPathPolicy
{
    private readonly string _runRoot;
    private readonly string _runRootWithSeparator;

    public RunPathPolicy(string runRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runRoot);
        _runRoot = Path.GetFullPath(runRoot);
        _runRootWithSeparator = _runRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _runRoot
            : _runRoot + Path.DirectorySeparatorChar;
    }

    public string Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw CreateOutsideRootException(relativePath);
        }

        string candidate = Path.GetFullPath(Path.Combine(_runRoot, relativePath));
        if (!candidate.StartsWith(_runRootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidate, _runRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw CreateOutsideRootException(relativePath);
        }

        return candidate;
    }

    private static InvalidOperationException CreateOutsideRootException(string suppliedPath) =>
        new($"{ArenaErrorCodes.PathOutsideRunRoot}: '{suppliedPath}' is not within the active run root.");
}
