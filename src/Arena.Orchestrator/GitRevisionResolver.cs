using System.Text.RegularExpressions;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Orchestrator;

/// <summary>
/// Reads the checked-out Git object identity without shelling out or including
/// a machine-local path in a benchmark artifact.
/// </summary>
public static partial class GitRevisionResolver
{
    public static string Resolve(string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.RunPreparationFailed}: the repository root is unavailable for manifest revision capture.");
        }

        string gitPath = Path.Combine(Path.GetFullPath(repositoryRoot), ".git");
        string gitDirectory = ResolveGitDirectory(gitPath);
        string headPath = Path.Combine(gitDirectory, "HEAD");
        if (!File.Exists(headPath))
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.RunPreparationFailed}: the repository Git HEAD is unavailable for manifest revision capture.");
        }

        string head = File.ReadAllText(headPath).Trim();
        string revision = head.StartsWith("ref: ", StringComparison.Ordinal)
            ? ResolveReference(gitDirectory, head[5..].Trim())
            : head;
        if (!GitRevisionPattern().IsMatch(revision))
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.RunPreparationFailed}: the repository Git revision is not a supported immutable object identifier.");
        }

        return revision;
    }

    private static string ResolveGitDirectory(string gitPath)
    {
        if (Directory.Exists(gitPath))
        {
            return gitPath;
        }

        if (!File.Exists(gitPath))
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.RunPreparationFailed}: the repository has no readable Git metadata.");
        }

        string marker = File.ReadAllText(gitPath).Trim();
        if (!marker.StartsWith("gitdir: ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.RunPreparationFailed}: the repository Git metadata pointer is invalid.");
        }

        string target = marker[8..].Trim();
        string directory = Path.IsPathRooted(target)
            ? Path.GetFullPath(target)
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(gitPath)!, target));
        if (!Directory.Exists(directory))
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.RunPreparationFailed}: the repository Git directory is unavailable.");
        }

        return directory;
    }

    private static string ResolveReference(string gitDirectory, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(reference))
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.RunPreparationFailed}: the repository Git reference is invalid.");
        }

        string referencePath = Path.GetFullPath(Path.Combine(gitDirectory, reference));
        string rootWithSeparator = gitDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? gitDirectory
            : gitDirectory + Path.DirectorySeparatorChar;
        if (!referencePath.StartsWith(rootWithSeparator, StringComparison.Ordinal) || !File.Exists(referencePath))
        {
            string packedPath = Path.Combine(gitDirectory, "packed-refs");
            if (File.Exists(packedPath))
            {
                foreach (string line in File.ReadLines(packedPath))
                {
                    string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length == 2 && string.Equals(fields[1], reference, StringComparison.Ordinal))
                    {
                        return fields[0];
                    }
                }
            }

            throw new InvalidOperationException($"{ArenaErrorCodes.RunPreparationFailed}: the repository Git reference target is unavailable.");
        }

        return File.ReadAllText(referencePath).Trim();
    }

    [GeneratedRegex("^[0-9a-f]{40,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex GitRevisionPattern();
}
