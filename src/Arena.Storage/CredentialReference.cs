namespace OpenTtd.ModelArena.Storage;

public sealed record CredentialReference
{
    public const string SchemePrefix = "credman:";
    public const string ArenaTargetPrefix = "OpenTTDModelArena/";

    private CredentialReference(string target)
    {
        Target = target;
    }

    public string Target { get; }

    public bool IsArenaManaged => IsArenaManagedTarget(Target);

    public override string ToString() => SchemePrefix + Target;

    public static bool TryParse(string? value, out CredentialReference? reference)
    {
        reference = null;
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith(SchemePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string target = value[SchemePrefix.Length..];
        if (target.Length is < 1 or > 256 ||
            !string.Equals(target, target.Trim(), StringComparison.Ordinal) ||
            target.Any(char.IsControl))
        {
            return false;
        }

        reference = new CredentialReference(target);
        return true;
    }

    public static bool IsArenaManagedTarget(string? target) =>
        !string.IsNullOrWhiteSpace(target) &&
        target.StartsWith(ArenaTargetPrefix, StringComparison.Ordinal) &&
        target.Length > ArenaTargetPrefix.Length &&
        target[ArenaTargetPrefix.Length..].All(character =>
            (character is >= 'A' and <= 'Z') ||
            (character is >= 'a' and <= 'z') ||
            (character is >= '0' and <= '9') ||
            character is '.' or '_' or '-');
}
