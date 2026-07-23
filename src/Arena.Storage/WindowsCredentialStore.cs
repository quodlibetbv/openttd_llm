using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Storage;

/// <summary>
/// A narrow Windows Credential Manager adapter. It only returns a secret to an
/// authenticated local caller as disposable byte material and never logs it.
/// </summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobBytes = 5120;

    public Task<CredentialOperationResult> SetAsync(
        CredentialReference reference,
        SecretMaterial secret,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(PlatformUnavailableOperation());
        }

        if (!reference.IsArenaManaged)
        {
            return Task.FromResult(new CredentialOperationResult(
                false,
                ArenaErrorCodes.CredentialReferenceInvalid,
                "Credential targets managed by this CLI must use OpenTTDModelArena/<name> with letters, digits, dot, underscore, or hyphen."));
        }

        if (!secret.HasValue || secret.Bytes.Length > MaximumCredentialBlobBytes)
        {
            return Task.FromResult(new CredentialOperationResult(
                false,
                ArenaErrorCodes.CredentialReferenceInvalid,
                "Enter a non-empty credential no longer than 5,120 bytes."));
        }

        IntPtr secretPointer = IntPtr.Zero;
        byte[] secretCopy = secret.Bytes.ToArray();
        try
        {
            secretPointer = Marshal.AllocCoTaskMem(secretCopy.Length);
            Marshal.Copy(secretCopy, 0, secretPointer, secretCopy.Length);

            NativeCredential credential = new()
            {
                Type = CredentialTypeGeneric,
                TargetName = reference.Target,
                CredentialBlobSize = checked((uint)secretCopy.Length),
                CredentialBlob = secretPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = "OpenTTD Model Arena",
            };

            if (!CredWrite(ref credential, 0))
            {
                return Task.FromResult(new CredentialOperationResult(
                    false,
                    ArenaErrorCodes.CredentialStoreUnavailable,
                    "Windows Credential Manager could not save the credential. Confirm that Credential Manager is available and try again."));
            }

            return Task.FromResult(new CredentialOperationResult(
                true,
                null,
                "Credential saved in Windows Credential Manager."));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretCopy);
            if (secretPointer != IntPtr.Zero)
            {
                Marshal.Copy(secretCopy, 0, secretPointer, secretCopy.Length);
                Marshal.FreeCoTaskMem(secretPointer);
            }
        }
    }

    public Task<CredentialReadResult> ReadAsync(
        CredentialReference reference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new CredentialReadResult(
                false,
                null,
                ArenaErrorCodes.CredentialStoreUnavailable,
                "Windows Credential Manager is required on the supported Windows host."));
        }

        if (!reference.IsArenaManaged)
        {
            return Task.FromResult(new CredentialReadResult(
                false,
                null,
                ArenaErrorCodes.CredentialReferenceInvalid,
                "Credential targets read by this CLI must use OpenTTDModelArena/<name> with letters, digits, dot, underscore, or hyphen."));
        }

        if (!CredRead(reference.Target, CredentialTypeGeneric, 0, out IntPtr credentialPointer))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound)
            {
                return Task.FromResult(new CredentialReadResult(
                    false,
                    null,
                    ArenaErrorCodes.CredentialMissing,
                    "The referenced credential is missing. Add it with the credentials set command."));
            }

            return Task.FromResult(new CredentialReadResult(
                false,
                null,
                ArenaErrorCodes.CredentialStoreUnavailable,
                "Windows Credential Manager could not read the credential. Confirm that it is available and try again."));
        }

        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return Task.FromResult(new CredentialReadResult(
                    false,
                    null,
                    ArenaErrorCodes.CredentialMissing,
                    "The referenced credential has no value. Replace it with the credentials set command."));
            }

            if (credential.CredentialBlobSize > MaximumCredentialBlobBytes)
            {
                return Task.FromResult(new CredentialReadResult(
                    false,
                    null,
                    ArenaErrorCodes.CredentialStoreUnavailable,
                    "The referenced credential exceeds the supported size. Replace it with a dedicated Arena credential."));
            }

            byte[] secretBytes = new byte[checked((int)credential.CredentialBlobSize)];
            try
            {
                Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length);
                return Task.FromResult(new CredentialReadResult(
                    true,
                    SecretMaterial.FromBytes(secretBytes),
                    null,
                    "Credential is available."));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secretBytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public Task<CredentialOperationResult> RemoveAsync(
        CredentialReference reference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(PlatformUnavailableOperation());
        }

        if (!reference.IsArenaManaged)
        {
            return Task.FromResult(new CredentialOperationResult(
                false,
                ArenaErrorCodes.CredentialReferenceInvalid,
                "Credential targets managed by this CLI must use OpenTTDModelArena/<name> with letters, digits, dot, underscore, or hyphen."));
        }

        if (CredDelete(reference.Target, CredentialTypeGeneric, 0) ||
            Marshal.GetLastPInvokeError() == ErrorNotFound)
        {
            return Task.FromResult(new CredentialOperationResult(
                true,
                null,
                "Credential removed, or it was already absent."));
        }

        return Task.FromResult(new CredentialOperationResult(
            false,
            ArenaErrorCodes.CredentialStoreUnavailable,
            "Windows Credential Manager could not remove the credential. Confirm that it is available and try again."));
    }

    public Task<CredentialListResult> ListArenaMetadataAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new CredentialListResult(
                false,
                [],
                ArenaErrorCodes.CredentialStoreUnavailable,
                "Windows Credential Manager is required on the supported Windows host."));
        }

        if (!CredEnumerate(CredentialReference.ArenaTargetPrefix + "*", 0, out uint count, out IntPtr credentialArray))
        {
            if (Marshal.GetLastPInvokeError() == ErrorNotFound)
            {
                return Task.FromResult(new CredentialListResult(true, [], null, "No Arena credentials are stored."));
            }

            return Task.FromResult(new CredentialListResult(
                false,
                [],
                ArenaErrorCodes.CredentialStoreUnavailable,
                "Windows Credential Manager could not list Arena credential metadata. Confirm that it is available and try again."));
        }

        if (count > 1024)
        {
            CredFree(credentialArray);
            return Task.FromResult(new CredentialListResult(
                false,
                [],
                ArenaErrorCodes.CredentialStoreUnavailable,
                "Too many Arena credential entries were returned. Remove stale entries and try again."));
        }

        try
        {
            List<CredentialMetadata> credentials = [];
            for (int index = 0; index < count; index++)
            {
                IntPtr credentialPointer = Marshal.ReadIntPtr(credentialArray, index * IntPtr.Size);
                NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
                if (!string.IsNullOrWhiteSpace(credential.TargetName) &&
                    CredentialReference.IsArenaManagedTarget(credential.TargetName))
                {
                    credentials.Add(new CredentialMetadata(
                        credential.TargetName,
                        ToDateTimeOffset(credential.LastWritten)));
                }
            }

            IReadOnlyList<CredentialMetadata> ordered = credentials
                .OrderBy(credential => credential.Target, StringComparer.Ordinal)
                .ToArray();
            return Task.FromResult(new CredentialListResult(
                true,
                ordered,
                null,
                ordered.Count == 0 ? "No Arena credentials are stored." : "Arena credential metadata listed."));
        }
        finally
        {
            CredFree(credentialArray);
        }
    }

    private static CredentialOperationResult PlatformUnavailableOperation() =>
        new(
            false,
            ArenaErrorCodes.CredentialStoreUnavailable,
            "Windows Credential Manager is required on the supported Windows host.");

    private static DateTimeOffset? ToDateTimeOffset(FILETIME fileTime)
    {
        long value = ((long)(uint)fileTime.dwHighDateTime << 32) | (uint)fileTime.dwLowDateTime;
        if (value <= 0)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(DateTime.FromFileTimeUtc(value));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string targetName,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string targetName, uint type, uint flags);

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(
        string filter,
        uint flags,
        out uint count,
        out IntPtr credentialArray);

    [DllImport("Advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? UserName;
    }
}
