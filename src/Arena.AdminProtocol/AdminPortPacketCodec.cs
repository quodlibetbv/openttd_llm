using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.AdminProtocol;

/// <summary>
/// Wire values from OpenTTD 14 and 15's tcp_admin.h. The Arena only uses the
/// GameScript, ping, and lifecycle subset; it never exposes RCON to a model or
/// provider adapter.
/// </summary>
public enum AdminPortPacketType : byte
{
    AdminJoin = 0,
    AdminQuit = 1,
    AdminUpdateFrequency = 2,
    AdminGameScript = 6,
    AdminPing = 7,
    AdminJoinSecure = 9,
    AdminAuthenticationResponse = 10,
    ServerFull = 100,
    ServerBanned = 101,
    ServerError = 102,
    ServerProtocol = 103,
    ServerWelcome = 104,
    ServerNewGame = 105,
    ServerShutdown = 106,
    ServerGameScript = 124,
    ServerPong = 126,
    ServerAuthenticationRequest = 128,
    ServerEnableEncryption = 129,
}

public enum AdminPortUpdateType : ushort
{
    GameScript = 9,
}

[Flags]
public enum AdminPortUpdateFrequency : ushort
{
    Automatic = 0x40,
}

public sealed record AdminPortPacket(AdminPortPacketType Type, ReadOnlyMemory<byte> Payload);

public sealed record AdminPortProtocolInfo(
    byte Version,
    IReadOnlyDictionary<AdminPortUpdateType, AdminPortUpdateFrequency> SupportedUpdates);

public static class AdminPortPacketCodec
{
    public const int OpenTtdAdminProtocolVersion = 3;
    public const int MaximumPacketBytes = 32_767;
    public const int MaximumGameScriptJsonBytes = 8 * 1024;
    public const int MaximumAdminPasswordBytes = 31;
    private const int PacketHeaderBytes = 3;

    public static byte[] EncodeAdminJoin(ReadOnlySpan<byte> password)
    {
        if (!IsSupportedPassword(password))
        {
            throw new ArgumentException("The AdminPort credential must be 1 to 31 printable ASCII characters.", nameof(password));
        }

        byte[] passwordCopy = password.ToArray();
        try
        {
            return Encode(
                AdminPortPacketType.AdminJoin,
                writer =>
                {
                    writer.Write(passwordCopy);
                    writer.WriteByte(0);
                    WriteNullTerminatedUtf8(writer, "OpenTTD Model Arena");
                    WriteNullTerminatedUtf8(writer, "phase-03");
                });
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(passwordCopy);
        }
    }

    public static byte[] EncodeAdminJoinSecure(ushort authenticationMethods) =>
        Encode(
            AdminPortPacketType.AdminJoinSecure,
            writer =>
            {
                WriteNullTerminatedUtf8(writer, "OpenTTD Model Arena");
                WriteNullTerminatedUtf8(writer, "phase-03");
                WriteUInt16(writer, authenticationMethods);
            });

    public static byte[] EncodeAdminAuthenticationResponse(
        ReadOnlySpan<byte> publicKey,
        ReadOnlySpan<byte> mac,
        ReadOnlySpan<byte> encryptedMessage)
    {
        if (publicKey.Length != 32 || mac.Length != 16 || encryptedMessage.Length != 8)
        {
            throw new ArgumentOutOfRangeException(nameof(publicKey), "The secure AdminPort response has invalid cryptographic field lengths.");
        }

        byte[] publicKeyCopy = publicKey.ToArray();
        byte[] macCopy = mac.ToArray();
        byte[] messageCopy = encryptedMessage.ToArray();
        try
        {
            return Encode(
                AdminPortPacketType.AdminAuthenticationResponse,
                writer =>
                {
                    writer.Write(publicKeyCopy);
                    writer.Write(macCopy);
                    writer.Write(messageCopy);
                });
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(publicKeyCopy);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(macCopy);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(messageCopy);
        }
    }

    public static byte[] EncodeGameScript(ReadOnlySpan<byte> json)
    {
        if (json.Length is 0 or >= MaximumGameScriptJsonBytes || json.Contains((byte)0))
        {
            throw new ArgumentOutOfRangeException(nameof(json), "GameScript messages must be non-empty UTF-8 JSON below the OpenTTD limit.");
        }

        byte[] jsonCopy = json.ToArray();
        try
        {
            return Encode(
                AdminPortPacketType.AdminGameScript,
                writer =>
                {
                    writer.Write(jsonCopy);
                    writer.WriteByte(0);
                });
        }
        finally
        {
            Array.Clear(jsonCopy, 0, jsonCopy.Length);
        }
    }

    public static byte[] EncodeGameScript(ProtocolEnvelope envelope)
    {
        ProtocolValidationResult validation = ProtocolEnvelopeValidator.Validate(envelope);
        if (!validation.IsValid)
        {
            throw new ArgumentException(validation.UserMessage, nameof(envelope));
        }

        byte[] json = ProtocolJson.Serialize(envelope);
        try
        {
            return EncodeGameScript(json);
        }
        finally
        {
            Array.Clear(json, 0, json.Length);
        }
    }

    public static byte[] EncodeUpdateFrequency(
        AdminPortUpdateType updateType,
        AdminPortUpdateFrequency frequency) =>
        Encode(
            AdminPortPacketType.AdminUpdateFrequency,
            writer =>
            {
                WriteUInt16(writer, (ushort)updateType);
                WriteUInt16(writer, (ushort)frequency);
            });

    public static byte[] EncodePing(uint nonce) =>
        Encode(
            AdminPortPacketType.AdminPing,
            writer => WriteUInt32(writer, nonce));

    public static byte[] EncodeQuit() => Encode(AdminPortPacketType.AdminQuit, static _ => { });

    public static Task<AdminPortPacket> ReadAsync(Stream stream, CancellationToken cancellationToken) =>
        ReadAsync(stream, encryption: null, cancellationToken: cancellationToken);

    internal static async Task<AdminPortPacket> ReadAsync(
        Stream stream,
        OpenTtdAdminRecordCipher? encryption,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] header = new byte[2];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int packetLength = BinaryPrimitives.ReadUInt16LittleEndian(header);
        if (packetLength < PacketHeaderBytes || packetLength > MaximumPacketBytes)
        {
            throw new AdminPortWireException(
                ArenaErrorCodes.ProtocolInvalidMessage,
                "OpenTTD AdminPort sent an invalid packet length.");
        }

        byte[] body = new byte[packetLength - 2];
        await stream.ReadExactlyAsync(body, cancellationToken);
        if (encryption is null)
        {
            return new AdminPortPacket((AdminPortPacketType)body[0], body.AsMemory(1));
        }

        if (body.Length <= 16 || !encryption.TryDecrypt(body.AsSpan(0, 16), body.AsSpan(16)))
        {
            throw new AdminPortWireException(
                ArenaErrorCodes.AdminPortAuthenticationFailed,
                "OpenTTD sent an AdminPort record that failed authentication.");
        }

        return new AdminPortPacket((AdminPortPacketType)body[16], body.AsMemory(17));
    }

    internal static byte[] EncryptPacket(byte[] plaintextPacket, OpenTtdAdminRecordCipher encryption)
    {
        ArgumentNullException.ThrowIfNull(plaintextPacket);
        ArgumentNullException.ThrowIfNull(encryption);
        if (plaintextPacket.Length < PacketHeaderBytes ||
            BinaryPrimitives.ReadUInt16LittleEndian(plaintextPacket) != plaintextPacket.Length ||
            plaintextPacket.Length + 16 > MaximumPacketBytes)
        {
            throw new ArgumentException("The plaintext AdminPort packet has invalid framing.", nameof(plaintextPacket));
        }

        byte[] encrypted = new byte[plaintextPacket.Length + 16];
        BinaryPrimitives.WriteUInt16LittleEndian(encrypted, checked((ushort)encrypted.Length));
        plaintextPacket.AsSpan(2).CopyTo(encrypted.AsSpan(18));
        encryption.Encrypt(encrypted.AsSpan(18), encrypted.AsSpan(2, 16));
        return encrypted;
    }

    public static bool TryParseServerProtocol(AdminPortPacket packet, out AdminPortProtocolInfo? protocol)
    {
        protocol = null;
        if (packet.Type != AdminPortPacketType.ServerProtocol || packet.Payload.Length < 2)
        {
            return false;
        }

        ReadOnlySpan<byte> data = packet.Payload.Span;
        int index = 0;
        byte version = data[index++];
        Dictionary<AdminPortUpdateType, AdminPortUpdateFrequency> updates = [];
        while (index < data.Length)
        {
            bool more = data[index++] != 0;
            if (!more)
            {
                if (index != data.Length)
                {
                    return false;
                }

                protocol = new AdminPortProtocolInfo(version, updates);
                return true;
            }

            if (index + 4 > data.Length)
            {
                return false;
            }

            AdminPortUpdateType type = (AdminPortUpdateType)BinaryPrimitives.ReadUInt16LittleEndian(data[index..]);
            index += 2;
            AdminPortUpdateFrequency frequencies = (AdminPortUpdateFrequency)BinaryPrimitives.ReadUInt16LittleEndian(data[index..]);
            index += 2;
            updates[type] = frequencies;
        }

        return false;
    }

    public static bool TryReadGameScriptJson(AdminPortPacket packet, out ReadOnlyMemory<byte> json)
    {
        json = ReadOnlyMemory<byte>.Empty;
        if (packet.Type != AdminPortPacketType.ServerGameScript ||
            packet.Payload.Length is 0 or >= MaximumGameScriptJsonBytes)
        {
            return false;
        }

        ReadOnlySpan<byte> payload = packet.Payload.Span;
        int nullIndex = payload.IndexOf((byte)0);
        if (nullIndex < 0 || nullIndex != payload.Length - 1)
        {
            return false;
        }

        json = packet.Payload[..nullIndex];
        return true;
    }

    public static bool TryReadPong(AdminPortPacket packet, out uint nonce)
    {
        nonce = 0;
        if (packet.Type != AdminPortPacketType.ServerPong || packet.Payload.Length != sizeof(uint))
        {
            return false;
        }

        nonce = BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.Span);
        return true;
    }

    public static bool TryReadServerError(AdminPortPacket packet, out byte error)
    {
        error = 0;
        if (packet.Type != AdminPortPacketType.ServerError || packet.Payload.Length != 1)
        {
            return false;
        }

        error = packet.Payload.Span[0];
        return true;
    }

    public static bool IsSupportedPassword(ReadOnlySpan<byte> password)
    {
        if (password.Length is 0 or > MaximumAdminPasswordBytes)
        {
            return false;
        }

        foreach (byte value in password)
        {
            if (value is < 0x21 or > 0x7e || value is (byte)'=' or (byte)';' or (byte)'#')
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] Encode(AdminPortPacketType type, Action<MemoryStream> writePayload)
    {
        using MemoryStream payload = new();
        payload.WriteByte((byte)type);
        writePayload(payload);
        if (payload.Length + 2 > MaximumPacketBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(writePayload), "The AdminPort packet exceeds the OpenTTD packet limit.");
        }

        byte[] result = new byte[payload.Length + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(result, checked((ushort)result.Length));
        payload.Position = 0;
        _ = payload.Read(result, 2, checked((int)payload.Length));
        return result;
    }

    private static void WriteNullTerminatedUtf8(Stream stream, string value)
    {
        byte[] encoded = Encoding.UTF8.GetBytes(value);
        try
        {
            stream.Write(encoded);
            stream.WriteByte(0);
        }
        finally
        {
            Array.Clear(encoded, 0, encoded.Length);
        }
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}

public sealed class AdminPortWireException : IOException
{
    public AdminPortWireException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

internal static class ProtocolJson
{
    private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public static byte[] Serialize(ProtocolEnvelope envelope) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
}
