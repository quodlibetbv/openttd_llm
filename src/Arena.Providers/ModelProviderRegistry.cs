using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Providers;

public sealed class ModelProviderRegistry
{
    private readonly Dictionary<string, IModelProvider> _providers;

    public ModelProviderRegistry(IEnumerable<IModelProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        Dictionary<string, IModelProvider> mapped = new(StringComparer.Ordinal);
        foreach (IModelProvider provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (!mapped.TryAdd(provider.Descriptor.ProviderId, provider))
            {
                throw new ArgumentException($"A provider named '{provider.Descriptor.ProviderId}' is already registered.", nameof(providers));
            }
        }

        _providers = mapped;
    }

    public IReadOnlyList<ProviderDescriptor> List() =>
        _providers.Values
            .Select(provider => provider.Descriptor)
            .OrderBy(descriptor => descriptor.ProviderId, StringComparer.Ordinal)
            .ToArray();

    public bool TryGet(string providerId, out IModelProvider? provider) =>
        _providers.TryGetValue(providerId, out provider);

    public static ProviderDecisionResult NotConfigured(string providerId) =>
        ProviderDecisionResult.Failed(
            new ArenaError(
                ArenaErrorCodes.ProviderNotConfigured,
                "The selected model provider is not configured for this run.",
                $"No provider registry entry exists for '{providerId}'.",
                false),
            ProviderUsage.Empty);
}
