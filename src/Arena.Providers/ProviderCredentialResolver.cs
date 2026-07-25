using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Providers;

public interface IProviderCredentialResolver
{
    Task<ProviderCredentialResolution> ResolveAsync(CancellationToken cancellationToken);
}

public sealed record ProviderCredentialResolution(SecretMaterial? Secret, ArenaError? Error)
{
    public bool Succeeded => Secret is { HasValue: true } && Error is null;

    public static ProviderCredentialResolution Success(SecretMaterial secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return new ProviderCredentialResolution(secret, null);
    }

    public static ProviderCredentialResolution Failure(ArenaError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ProviderCredentialResolution(null, error);
    }
}
