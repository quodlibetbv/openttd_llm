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

        EnsureNoReparsePoints(candidate);
        return candidate;
    }

    /// <summary>
    /// Creates a directory only after proving that every existing path segment
    /// from the configured root to the target is not a symlink or junction.
    /// </summary>
    public string CreateDirectory(string relativePath)
    {
        string path = Resolve(relativePath);
        Directory.CreateDirectory(path);
        EnsureNoReparsePoints(path);
        return path;
    }

    /// <summary>
    /// Rechecks a path that was resolved earlier before a sensitive operation
    /// such as allocation, copy, or deletion.
    /// </summary>
    public void EnsureSafePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string candidate = Path.GetFullPath(path);
        if (!candidate.StartsWith(_runRootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidate, _runRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw CreateOutsideRootException(path);
        }

        EnsureNoReparsePoints(candidate);
    }

    private void EnsureNoReparsePoints(string candidate)
    {
        EnsurePathSegmentIsNotReparsePoint(_runRoot);
        string relativePath = Path.GetRelativePath(_runRoot, candidate);
        string currentPath = _runRoot;
        foreach (string segment in relativePath.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            EnsurePathSegmentIsNotReparsePoint(currentPath);
        }
    }

    private static void EnsurePathSegmentIsNotReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"{ArenaErrorCodes.PathOutsideRunRoot}: the active run root must not traverse a symbolic link or junction.");
        }
    }

    private static InvalidOperationException CreateOutsideRootException(string suppliedPath) =>
        new($"{ArenaErrorCodes.PathOutsideRunRoot}: '{suppliedPath}' is not within the active run root.");
}
