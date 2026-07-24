using System.Text;
using OpenTtd.ModelArena.AdminProtocol;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Orchestrator;

/// <summary>
/// Materializes the OpenTTD-only AdminPort password in a per-run secrets.cfg.
/// The value exists only while the isolated server is running and is deleted
/// before any run artifact is indexed or reported.
/// </summary>
public static class AdminPortSecretFile
{
    private static readonly byte[] Prefix = Encoding.ASCII.GetBytes(
        "[version]\nini_version = 7\n\n[network]\nadmin_password = ");
    private static readonly byte[] Suffix = Encoding.ASCII.GetBytes("\n");

    public static async Task<string> WriteAsync(
        RunPathPolicy paths,
        string relativeServerDirectory,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeServerDirectory);
        if (!AdminPortPacketCodec.IsSupportedPassword(secret.Span))
        {
            throw new InvalidOperationException(
                $"{ArenaErrorCodes.AdminPortSecretInvalid}: the dedicated AdminPort credential must be 1 to 31 printable ASCII characters and must not contain spaces, equals, semicolons, or number signs.");
        }

        string path = paths.Resolve(Path.Combine(relativeServerDirectory, ArenaRuntimeLayout.SecretsConfigurationFileName));
        byte[] contents = new byte[Prefix.Length + secret.Length + Suffix.Length];
        try
        {
            Buffer.BlockCopy(Prefix, 0, contents, 0, Prefix.Length);
            secret.CopyTo(contents.AsMemory(Prefix.Length, secret.Length));
            Buffer.BlockCopy(Suffix, 0, contents, Prefix.Length + secret.Length, Suffix.Length);
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException($"{ArenaErrorCodes.RunPreparationFailed}: AdminPort secret path has no parent directory.");
            }

            Directory.CreateDirectory(directory);
            await using FileStream stream = new(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough);
            await stream.WriteAsync(contents, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
            return path;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(contents);
        }
    }

    public static void Delete(RunPathPolicy paths, string path)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        paths.EnsureSafePath(path);
        if (!File.Exists(path))
        {
            return;
        }

        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
        if (File.Exists(path))
        {
            throw new IOException("The per-run AdminPort secret file could not be removed.");
        }
    }
}
