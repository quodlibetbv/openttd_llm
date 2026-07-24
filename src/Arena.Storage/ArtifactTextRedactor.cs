using System.Text.RegularExpressions;

namespace OpenTtd.ModelArena.Storage;

/// <summary>
/// Redacts text that is persisted below a run directory. Unlike interactive
/// command output, run artifacts must not disclose machine-local paths.
/// </summary>
public static partial class ArtifactTextRedactor
{
    private const string LocalPathReplacement = "[LOCAL-PATH]";

    public static string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return WindowsMachinePathPattern().Replace(SecretRedactor.Redact(value), LocalPathReplacement);
    }

    [GeneratedRegex(@"(?i)(?:[a-z]:[\\/]|\\\\)[^\r\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsMachinePathPattern();
}
