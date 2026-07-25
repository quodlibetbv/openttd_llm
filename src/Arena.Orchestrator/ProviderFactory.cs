using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Providers;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Orchestrator;

public sealed class CredentialStoreProviderCredentialResolver : IProviderCredentialResolver
{
    private readonly ICredentialStore _credentialStore;
    private readonly CredentialReference _reference;

    public CredentialStoreProviderCredentialResolver(ICredentialStore credentialStore, CredentialReference reference)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(reference);
        _credentialStore = credentialStore;
        _reference = reference;
    }

    public async Task<ProviderCredentialResolution> ResolveAsync(CancellationToken cancellationToken)
    {
        CredentialReadResult result = await _credentialStore.ReadAsync(_reference, cancellationToken);
        return result.Succeeded && result.Secret is not null
            ? ProviderCredentialResolution.Success(result.Secret)
            : ProviderCredentialResolution.Failure(new ArenaError(
                result.ErrorCode ?? ArenaErrorCodes.CredentialMissing,
                "The configured provider credential is unavailable.",
                "Windows Credential Manager could not resolve the configured provider credential.",
                false));
    }
}

public sealed record ProviderCreationResult(IModelProvider? Provider, ArenaError? Error)
{
    public bool Succeeded => Provider is not null && Error is null;
}

/// <summary>
/// Maps machine-local provider metadata to a provider-neutral adapter without
/// leaking Credential Manager or configuration types into Arena.Providers.
/// </summary>
public sealed class ModelProviderFactory
{
    private static readonly Uri DeepSeekDefaultBaseUri = new("https://api.deepseek.com/");
    private readonly ICredentialStore _credentialStore;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public ModelProviderFactory(
        ICredentialStore credentialStore,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);
        _credentialStore = credentialStore;
        _httpClient = httpClient ?? new HttpClient();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ProviderCreationResult Create(ProviderLocalConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!string.Equals(configuration.Type, "deepseek", StringComparison.Ordinal))
        {
            return Failure("The configured provider type is not supported by this Arena build.");
        }

        if (configuration.CredentialReference is null || string.IsNullOrWhiteSpace(configuration.Model))
        {
            return Failure("A DeepSeek provider requires both model and credential_ref metadata.");
        }

        try
        {
            DeepSeekProviderOptions options = new(
                configuration.BaseUri ?? DeepSeekDefaultBaseUri,
                configuration.Model,
                TimeSpan.FromSeconds(configuration.TimeoutSeconds),
                configuration.MaximumTransientRetries,
                configuration.InputCostPerMillionTokens,
                configuration.OutputCostPerMillionTokens,
                configuration.Id);
            IProviderCredentialResolver credentialResolver = new CredentialStoreProviderCredentialResolver(
                _credentialStore,
                configuration.CredentialReference);
            return new ProviderCreationResult(
                new DeepSeekModelProvider(_httpClient, credentialResolver, options, _timeProvider),
                null);
        }
        catch (ArgumentException)
        {
            return Failure("The configured DeepSeek provider metadata is invalid.");
        }
    }

    private static ProviderCreationResult Failure(string message) =>
        new(
            null,
            new ArenaError(
                ArenaErrorCodes.ProviderConfigurationInvalid,
                message,
                "Provider construction was rejected before any credential or network request was made.",
                false));
}
