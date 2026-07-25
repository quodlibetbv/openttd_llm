using System.Text;

namespace OpenTtd.ModelArena.Contracts;

/// <summary>
/// Keeps publication-bound provider and GameScript text bounded and free of
/// control characters or markup delimiters. This is not an HTML renderer; the
/// overlay still escapes text at its own boundary.
/// </summary>
public static class PublicTextSanitizer
{
    public static string Sanitize(string? value, int maximumLength, string fallback)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);

        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback.Length <= maximumLength ? fallback : fallback[..maximumLength];
        }

        StringBuilder builder = new(Math.Min(value.Length, maximumLength));
        foreach (char character in value.Trim())
        {
            if (builder.Length >= maximumLength)
            {
                break;
            }

            if (char.IsControl(character))
            {
                continue;
            }

            builder.Append(character switch
            {
                '<' => '‹',
                '>' => '›',
                _ => character,
            });
        }

        return builder.Length == 0 ? fallback : builder.ToString();
    }
}
