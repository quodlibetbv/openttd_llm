using System.Security.Cryptography;

namespace OpenTtd.ModelArena.Contracts;

public enum DoctorCheckStatus
{
    Pass,
    Warning,
    BlockingFailure,
}

public sealed record DoctorCheckResult(
    string Id,
    DoctorCheckStatus Status,
    string Code,
    string Summary,
    string Remediation,
    string? Detail = null);

public sealed record DoctorReport(
    int ReportVersion,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<DoctorCheckResult> Checks)
{
    public bool HasBlockingFailures => Checks.Any(check => check.Status == DoctorCheckStatus.BlockingFailure);
}

/// <summary>
/// Holds credential bytes only for the duration of an authenticated local operation.
/// Callers must dispose it and must never render or log its contents.
/// </summary>
public sealed class SecretMaterial : IDisposable
{
    private byte[]? _bytes;

    private SecretMaterial(byte[] bytes)
    {
        _bytes = bytes;
    }

    public bool HasValue => _bytes is { Length: > 0 };

    public ReadOnlyMemory<byte> Bytes => _bytes ?? ReadOnlyMemory<byte>.Empty;

    public static SecretMaterial FromUtf8(ReadOnlySpan<char> value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Length, 5120);
        char[] copy = value.ToArray();
        try
        {
            return new SecretMaterial(System.Text.Encoding.UTF8.GetBytes(copy));
        }
        finally
        {
            Array.Clear(copy, 0, copy.Length);
        }
    }

    public static SecretMaterial FromBytes(ReadOnlySpan<byte> value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Length, 5120);
        return new SecretMaterial(value.ToArray());
    }

    public void Dispose()
    {
        if (_bytes is not null)
        {
            CryptographicOperations.ZeroMemory(_bytes);
            _bytes = null;
        }

        GC.SuppressFinalize(this);
    }
}
