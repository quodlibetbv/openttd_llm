using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Storage;

public interface ICredentialStore
{
    Task<CredentialOperationResult> SetAsync(
        CredentialReference reference,
        SecretMaterial secret,
        CancellationToken cancellationToken);

    Task<CredentialReadResult> ReadAsync(
        CredentialReference reference,
        CancellationToken cancellationToken);

    Task<CredentialOperationResult> RemoveAsync(
        CredentialReference reference,
        CancellationToken cancellationToken);

    Task<CredentialListResult> ListArenaMetadataAsync(CancellationToken cancellationToken);
}

public sealed record CredentialOperationResult(
    bool Succeeded,
    string? ErrorCode,
    string UserMessage);

public sealed record CredentialReadResult(
    bool Succeeded,
    SecretMaterial? Secret,
    string? ErrorCode,
    string UserMessage);

public sealed record CredentialMetadata(string Target, DateTimeOffset? LastWrittenUtc);

public sealed record CredentialListResult(
    bool Succeeded,
    IReadOnlyList<CredentialMetadata> Credentials,
    string? ErrorCode,
    string UserMessage);
