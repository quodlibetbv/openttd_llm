using System.Text.RegularExpressions;

namespace OpenTtd.ModelArena.Storage;

public static partial class SecretRedactor
{
    private const string Replacement = "[REDACTED]";

    public static string Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        string redacted = BearerPattern().Replace(value, $"Bearer {Replacement}");
        redacted = OpenAiStyleTokenPattern().Replace(redacted, Replacement);
        return NamedSecretPattern().Replace(redacted, match => $"{match.Groups[1].Value}{Replacement}");
    }

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~-]{8,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9]{20,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiStyleTokenPattern();

    [GeneratedRegex(@"(?i)(\b(?:api[_-]?key|password|secret|token|authorization)\s*[:=]\s*)([^\s,;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex NamedSecretPattern();
}
