using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.AdminProtocol;

public interface IAdminPortTransport
{
    Task<AdminPortSendResult> SendAsync(
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken);
}

public sealed record AdminPortSendResult(
    bool Accepted,
    string? ErrorCode,
    string? TechnicalMessage);

public interface IProtocolCompatibilityVerifier
{
    ProtocolCompatibilityResult Verify(string localVersion, string remoteVersion);
}

public sealed record ProtocolCompatibilityResult(
    bool IsCompatible,
    string? ErrorCode,
    string UserMessage);
