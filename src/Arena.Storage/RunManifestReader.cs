using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Storage;

/// <summary>
/// Reads a manifest only after callers have established their desired trust
/// level (for example with <see cref="RunVerifier"/>). Keeping the strict
/// deserializer here prevents replay and reporting commands from hand-parsing
/// an immutable run identity.
/// </summary>
public static class RunManifestReader
{
    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<RunManifest> ReadAsync(RunPathPolicy paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string path = paths.Resolve(RunManifestFinalizer.ManifestFileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.RunArtifactMissing}: the immutable run manifest is absent.");
        }

        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            RunManifest? manifest = await JsonSerializer.DeserializeAsync<RunManifest>(stream, StrictJsonOptions, cancellationToken);
            return manifest ?? throw new InvalidOperationException($"{ArenaErrorCodes.ArtifactVerificationFailed}: the immutable run manifest is empty.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{ArenaErrorCodes.ArtifactVerificationFailed}: the immutable run manifest is not a closed supported JSON contract.");
        }
    }
}
