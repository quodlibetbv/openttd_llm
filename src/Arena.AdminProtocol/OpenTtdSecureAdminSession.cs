using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC.Rfc7748;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.AdminProtocol;

/// <summary>
/// Implements OpenTTD 15.x's password-authenticated X25519 AdminPort login.
/// The server upgrades both directions to its rekeying encrypted record format
/// immediately after authentication, so no Arena envelope or credential crosses
/// an OpenTTD 15.x socket in clear text.
/// </summary>
internal sealed class OpenTtdSecureAdminSession : IDisposable
{
    private const byte X25519PakeAuthenticationMethod = 1;
    private const int KeyBytes = 32;
    private const int NonceBytes = 24;
    private const int MacBytes = 16;
    private const int KeyExchangeMessageBytes = 8;
    private readonly byte[] _password;
    private byte[]? _clientToServerKey;
    private byte[]? _serverToClientKey;
    private bool _disposed;

    public OpenTtdSecureAdminSession(ReadOnlySpan<byte> password)
    {
        if (!AdminPortPacketCodec.IsSupportedPassword(password))
        {
            throw new ArgumentException("The AdminPort credential must be a dedicated supported secret.", nameof(password));
        }

        _password = password.ToArray();
    }

    public static byte[] CreateJoinPacket() => AdminPortPacketCodec.EncodeAdminJoinSecure(
        authenticationMethods: 1 << X25519PakeAuthenticationMethod);

    public byte[] CreateAuthenticationResponse(AdminPortPacket packet)
    {
        ThrowIfDisposed();
        if (packet.Type != AdminPortPacketType.ServerAuthenticationRequest ||
            packet.Payload.Length != 1 + KeyBytes + NonceBytes ||
            packet.Payload.Span[0] != X25519PakeAuthenticationMethod ||
            _clientToServerKey is not null ||
            _serverToClientKey is not null)
        {
            throw new AdminPortWireException(
                ArenaErrorCodes.AdminPortProtocolIncompatible,
                "OpenTTD sent an unsupported secure AdminPort authentication request.");
        }

        byte[] serverPublicKey = packet.Payload.Slice(1, KeyBytes).ToArray();
        byte[] authenticationNonce = packet.Payload.Slice(1 + KeyBytes, NonceBytes).ToArray();
        byte[] clientSecretKey = new byte[KeyBytes];
        byte[] clientPublicKey = new byte[KeyBytes];
        byte[] sharedSecret = new byte[KeyBytes];
        byte[] derivedKeys = new byte[KeyBytes * 2];
        byte[] challengeMessage = new byte[KeyExchangeMessageBytes];
        byte[] mac = new byte[MacBytes];
        try
        {
            RandomNumberGenerator.Fill(clientSecretKey);
            X25519.GeneratePublicKey(clientSecretKey, 0, clientPublicKey, 0);
            if (!X25519.CalculateAgreement(clientSecretKey, 0, serverPublicKey, 0, sharedSecret, 0) ||
                sharedSecret.All(value => value == 0))
            {
                throw new AdminPortWireException(
                    ArenaErrorCodes.AdminPortAuthenticationFailed,
                    "OpenTTD supplied an invalid secure AdminPort key exchange value.");
            }

            DeriveKeys(sharedSecret, serverPublicKey, clientPublicKey, _password, derivedKeys);
            RandomNumberGenerator.Fill(challengeMessage);
            OpenTtdAdminRecordCipher.EncryptOneShot(
                derivedKeys.AsSpan(0, KeyBytes),
                authenticationNonce,
                clientPublicKey,
                challengeMessage,
                mac);

            _clientToServerKey = derivedKeys[..KeyBytes].ToArray();
            _serverToClientKey = derivedKeys[KeyBytes..].ToArray();
            return AdminPortPacketCodec.EncodeAdminAuthenticationResponse(clientPublicKey, mac, challengeMessage);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serverPublicKey);
            CryptographicOperations.ZeroMemory(authenticationNonce);
            CryptographicOperations.ZeroMemory(clientSecretKey);
            CryptographicOperations.ZeroMemory(clientPublicKey);
            CryptographicOperations.ZeroMemory(sharedSecret);
            CryptographicOperations.ZeroMemory(derivedKeys);
            CryptographicOperations.ZeroMemory(challengeMessage);
            CryptographicOperations.ZeroMemory(mac);
        }
    }

    public (OpenTtdAdminRecordCipher Send, OpenTtdAdminRecordCipher Receive) CreateRecordCiphers(AdminPortPacket packet)
    {
        ThrowIfDisposed();
        if (packet.Type != AdminPortPacketType.ServerEnableEncryption ||
            packet.Payload.Length != NonceBytes ||
            _clientToServerKey is null ||
            _serverToClientKey is null)
        {
            throw new AdminPortWireException(
                ArenaErrorCodes.AdminPortProtocolIncompatible,
                "OpenTTD sent an invalid secure AdminPort encryption transition.");
        }

        byte[] nonce = packet.Payload.ToArray();
        try
        {
            OpenTtdAdminRecordCipher send = new(_clientToServerKey, nonce);
            OpenTtdAdminRecordCipher receive = new(_serverToClientKey, nonce);
            CryptographicOperations.ZeroMemory(_clientToServerKey);
            CryptographicOperations.ZeroMemory(_serverToClientKey);
            _clientToServerKey = null;
            _serverToClientKey = null;
            return (send, receive);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_password);
        if (_clientToServerKey is not null)
        {
            CryptographicOperations.ZeroMemory(_clientToServerKey);
        }

        if (_serverToClientKey is not null)
        {
            CryptographicOperations.ZeroMemory(_serverToClientKey);
        }
    }

    private static void DeriveKeys(
        ReadOnlySpan<byte> sharedSecret,
        ReadOnlySpan<byte> serverPublicKey,
        ReadOnlySpan<byte> clientPublicKey,
        ReadOnlySpan<byte> password,
        Span<byte> output)
    {
        if (output.Length != KeyBytes * 2)
        {
            throw new ArgumentOutOfRangeException(nameof(output));
        }

        Blake2bDigest digest = new(512);
        Update(digest, sharedSecret);
        Update(digest, serverPublicKey);
        Update(digest, clientPublicKey);
        Update(digest, password);
        byte[] result = new byte[KeyBytes * 2];
        try
        {
            int written = digest.DoFinal(result, 0);
            if (written != output.Length)
            {
                throw new CryptographicException("The secure AdminPort key derivation produced an invalid length.");
            }

            result.CopyTo(output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(result);
        }
    }

    private static void Update(Blake2bDigest digest, ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        byte[] copy = input.ToArray();
        try
        {
            digest.BlockUpdate(copy, 0, copy.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

/// <summary>
/// OpenTTD uses Monocypher's rekeying XChaCha20/Poly1305 record construction,
/// not a one-shot AEAD for each packet. This mirrors the published OpenTTD
/// 15.3 implementation exactly: no associated data, a 16-byte MAC prefix, and
/// a new key derived from every authenticated record.
/// </summary>
internal sealed class OpenTtdAdminRecordCipher : IDisposable
{
    private const int KeyBytes = 32;
    private const int NonceBytes = 24;
    private const int MacBytes = 16;
    private readonly byte[] _nonce;
    private byte[] _key;
    private bool _disposed;

    public OpenTtdAdminRecordCipher(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
    {
        if (key.Length != KeyBytes || nonce.Length != NonceBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(key), "OpenTTD secure AdminPort keys and nonces have fixed lengths.");
        }

        _key = HChaCha20(key, nonce[..16]);
        _nonce = nonce[16..].ToArray();
    }

    public void Encrypt(Span<byte> message, Span<byte> mac)
    {
        ThrowIfDisposed();
        if (mac.Length != MacBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(mac));
        }

        byte[] authenticationMaterial = GenerateKeystream(_key, _nonce, 0, 64);
        byte[] plaintext = message.ToArray();
        byte[] ciphertext = GenerateKeystreamXor(_key, _nonce, 1, plaintext);
        try
        {
            CalculateMac(authenticationMaterial.AsSpan(0, KeyBytes), ReadOnlySpan<byte>.Empty, ciphertext, mac);
            ciphertext.CopyTo(message);
            Rekey(authenticationMaterial);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(authenticationMaterial);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public bool TryDecrypt(ReadOnlySpan<byte> mac, Span<byte> message)
    {
        ThrowIfDisposed();
        if (mac.Length != MacBytes)
        {
            return false;
        }

        byte[] authenticationMaterial = GenerateKeystream(_key, _nonce, 0, 64);
        byte[] ciphertext = message.ToArray();
        byte[] expectedMac = new byte[MacBytes];
        try
        {
            CalculateMac(authenticationMaterial.AsSpan(0, KeyBytes), ReadOnlySpan<byte>.Empty, ciphertext, expectedMac);
            if (!CryptographicOperations.FixedTimeEquals(mac, expectedMac))
            {
                return false;
            }

            byte[] plaintext = GenerateKeystreamXor(_key, _nonce, 1, ciphertext);
            try
            {
                plaintext.CopyTo(message);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            Rekey(authenticationMaterial);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(authenticationMaterial);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(expectedMac);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
        CryptographicOperations.ZeroMemory(_nonce);
    }

    /// <summary>
    /// Implements OpenTTD's one-shot XChaCha20/Poly1305 authentication record
    /// used during the PAKE challenge. Unlike encrypted AdminPort packets, the
    /// challenge does not retain or rekey a record context.
    /// </summary>
    internal static void EncryptOneShot(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> associatedData,
        Span<byte> message,
        Span<byte> mac)
    {
        if (key.Length != KeyBytes || nonce.Length != NonceBytes || mac.Length != MacBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(key), "OpenTTD secure AdminPort cryptographic fields have invalid lengths.");
        }

        byte[] subKey = HChaCha20(key, nonce[..16]);
        byte[] shortNonce = nonce[16..].ToArray();
        byte[] authenticationMaterial = GenerateKeystream(subKey, shortNonce, 0, 64);
        byte[] plaintext = message.ToArray();
        byte[] ciphertext = GenerateKeystreamXor(subKey, shortNonce, 1, plaintext);
        try
        {
            CalculateMac(authenticationMaterial.AsSpan(0, KeyBytes), associatedData, ciphertext, mac);
            ciphertext.CopyTo(message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(subKey);
            CryptographicOperations.ZeroMemory(shortNonce);
            CryptographicOperations.ZeroMemory(authenticationMaterial);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private static byte[] GenerateKeystream(byte[] key, byte[] nonce, long counter, int length)
    {
        byte[] zeros = new byte[length];
        try
        {
            return GenerateKeystreamXor(key, nonce, counter, zeros);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(zeros);
        }
    }

    private static byte[] GenerateKeystreamXor(byte[] key, byte[] nonce, long counter, byte[] input)
    {
        ChaChaEngine engine = new();
        engine.Init(true, new ParametersWithIV(new KeyParameter(key), nonce));
        if (counter > 0)
        {
            byte[] discarded = new byte[checked((int)(counter * 64))];
            try
            {
                engine.ProcessBytes(discarded, 0, discarded.Length, discarded, 0);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(discarded);
            }
        }

        byte[] output = new byte[input.Length];
        engine.ProcessBytes(input, 0, input.Length, output, 0);
        return output;
    }

    private static void CalculateMac(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> associatedData,
        ReadOnlySpan<byte> ciphertext,
        Span<byte> output)
    {
        byte[] keyCopy = key.ToArray();
        byte[] associatedDataCopy = associatedData.ToArray();
        byte[] ciphertextCopy = ciphertext.ToArray();
        byte[] lengths = new byte[16];
        byte[] tag = new byte[MacBytes];
        try
        {
            BinaryPrimitives.WriteUInt64LittleEndian(lengths, checked((ulong)associatedDataCopy.Length));
            BinaryPrimitives.WriteUInt64LittleEndian(lengths.AsSpan(sizeof(ulong)), checked((ulong)ciphertextCopy.Length));
            Poly1305 poly1305 = new();
            poly1305.Init(new KeyParameter(keyCopy));
            if (associatedDataCopy.Length > 0)
            {
                poly1305.BlockUpdate(associatedDataCopy, 0, associatedDataCopy.Length);
            }

            int associatedDataPadding = (16 - (associatedDataCopy.Length % 16)) % 16;
            if (associatedDataPadding > 0)
            {
                byte[] zeroPadding = new byte[associatedDataPadding];
                try
                {
                    poly1305.BlockUpdate(zeroPadding, 0, zeroPadding.Length);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(zeroPadding);
                }
            }

            if (ciphertextCopy.Length > 0)
            {
                poly1305.BlockUpdate(ciphertextCopy, 0, ciphertextCopy.Length);
            }

            int padding = (16 - (ciphertextCopy.Length % 16)) % 16;
            if (padding > 0)
            {
                byte[] zeroPadding = new byte[padding];
                try
                {
                    poly1305.BlockUpdate(zeroPadding, 0, zeroPadding.Length);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(zeroPadding);
                }
            }

            poly1305.BlockUpdate(lengths, 0, lengths.Length);
            _ = poly1305.DoFinal(tag, 0);
            tag.CopyTo(output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyCopy);
            CryptographicOperations.ZeroMemory(associatedDataCopy);
            CryptographicOperations.ZeroMemory(ciphertextCopy);
            CryptographicOperations.ZeroMemory(lengths);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    private void Rekey(ReadOnlySpan<byte> authenticationMaterial)
    {
        authenticationMaterial.Slice(KeyBytes, KeyBytes).CopyTo(_key);
    }

    private static byte[] HChaCha20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce)
    {
        if (key.Length != KeyBytes || nonce.Length != 16)
        {
            throw new ArgumentOutOfRangeException(nameof(key), "HChaCha20 requires a 32-byte key and 16-byte nonce prefix.");
        }

        Span<uint> state = stackalloc uint[16];
        state[0] = 0x61707865;
        state[1] = 0x3320646e;
        state[2] = 0x79622d32;
        state[3] = 0x6b206574;
        for (int index = 0; index < 8; index++)
        {
            state[4 + index] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(index * sizeof(uint), sizeof(uint)));
        }

        for (int index = 0; index < 4; index++)
        {
            state[12 + index] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(index * sizeof(uint), sizeof(uint)));
        }

        for (int round = 0; round < 10; round++)
        {
            QuarterRound(ref state[0], ref state[4], ref state[8], ref state[12]);
            QuarterRound(ref state[1], ref state[5], ref state[9], ref state[13]);
            QuarterRound(ref state[2], ref state[6], ref state[10], ref state[14]);
            QuarterRound(ref state[3], ref state[7], ref state[11], ref state[15]);
            QuarterRound(ref state[0], ref state[5], ref state[10], ref state[15]);
            QuarterRound(ref state[1], ref state[6], ref state[11], ref state[12]);
            QuarterRound(ref state[2], ref state[7], ref state[8], ref state[13]);
            QuarterRound(ref state[3], ref state[4], ref state[9], ref state[14]);
        }

        byte[] output = new byte[KeyBytes];
        uint[] words = [state[0], state[1], state[2], state[3], state[12], state[13], state[14], state[15]];
        for (int index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(index * sizeof(uint), sizeof(uint)), words[index]);
        }

        return output;
    }

    private static void QuarterRound(ref uint a, ref uint b, ref uint c, ref uint d)
    {
        a += b;
        d = BitOperations.RotateLeft(d ^ a, 16);
        c += d;
        b = BitOperations.RotateLeft(b ^ c, 12);
        a += b;
        d = BitOperations.RotateLeft(d ^ a, 8);
        c += d;
        b = BitOperations.RotateLeft(b ^ c, 7);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
