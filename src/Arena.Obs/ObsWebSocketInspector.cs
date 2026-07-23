using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Obs;

public interface IObsWebSocketInspector
{
    Task<ObsWebSocketInspectionResult> InspectAsync(
        ObsWebSocketInspectionRequest request,
        CancellationToken cancellationToken);
}

public sealed record ObsWebSocketInspectionRequest(
    string Host,
    int Port,
    ReadOnlyMemory<byte> Password,
    string ExpectedSceneCollection);

public sealed record ObsWebSocketInspectionResult(
    bool Succeeded,
    ObsSceneInventory? Inventory,
    string? ErrorCode,
    string UserMessage);

/// <summary>
/// Inspects only the OBS WebSocket 5.x handshake and scene inventory. It does
/// not start recordings, switch scenes, or mutate the user's OBS profile.
/// </summary>
public sealed class ObsWebSocketInspector : IObsWebSocketInspector
{
    private const int LatestSupportedRpcVersion = 1;
    private const int MaximumMessageBytes = 128 * 1024;
    private const int MaximumPasswordBytes = 5120;
    private const int MaximumSceneCollectionNameLength = 120;
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);

    public async Task<ObsWebSocketInspectionResult> InspectAsync(
        ObsWebSocketInspectionRequest request,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(request.Host, out IPAddress? address) ||
            !IPAddress.IsLoopback(address) ||
            request.Port is < 1 or > 65535)
        {
            return Failure(
                ArenaErrorCodes.ObsWebSocketUnavailable,
                "The OBS WebSocket endpoint is invalid. Restore the local configuration and use a loopback host and port.");
        }

        if (request.Password.IsEmpty || request.Password.Length > MaximumPasswordBytes)
        {
            return Failure(
                ArenaErrorCodes.CredentialMissing,
                "The OBS credential is empty. Replace it with a dedicated OBS WebSocket password in Windows Credential Manager.");
        }

        if (string.IsNullOrWhiteSpace(request.ExpectedSceneCollection) ||
            request.ExpectedSceneCollection.Length > MaximumSceneCollectionNameLength ||
            request.ExpectedSceneCollection.Any(char.IsControl))
        {
            return Failure(
                ArenaErrorCodes.ObsWebSocketUnavailable,
                "The configured OBS scene collection name is invalid. Restore the local configuration and try doctor again.");
        }

        using ClientWebSocket socket = new();
        socket.Options.AddSubProtocol("obswebsocket.json");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectionTimeout);
        bool awaitingIdentification = false;

        try
        {
            await socket.ConnectAsync(CreateEndpoint(request.Host, request.Port), timeout.Token);
            using JsonDocument hello = await ReceiveJsonAsync(socket, timeout.Token);
            if (!TryGetData(hello.RootElement, 0, out JsonElement helloData) ||
                !helloData.TryGetProperty("rpcVersion", out JsonElement rpcVersionElement) ||
                !rpcVersionElement.TryGetInt32(out int serverRpcVersion))
            {
                return Failure(
                    ArenaErrorCodes.ObsWebSocketUnavailable,
                    "OBS returned an invalid WebSocket hello message. Confirm OBS 28 or later with WebSocket 5.x is running.");
            }

            if (serverRpcVersion < LatestSupportedRpcVersion ||
                !helloData.TryGetProperty("authentication", out JsonElement authenticationElement) ||
                authenticationElement.ValueKind != JsonValueKind.Object ||
                !authenticationElement.TryGetProperty("challenge", out JsonElement challengeElement) ||
                !authenticationElement.TryGetProperty("salt", out JsonElement saltElement))
            {
                return Failure(
                    ArenaErrorCodes.ObsAuthenticationFailed,
                    "Enable OBS WebSocket authentication and use OBS WebSocket 5.x, then save its dedicated password with the credentials set command.");
            }

            string? challenge = challengeElement.GetString();
            string? salt = saltElement.GetString();
            if (string.IsNullOrWhiteSpace(challenge) || string.IsNullOrWhiteSpace(salt))
            {
                return Failure(
                    ArenaErrorCodes.ObsAuthenticationFailed,
                    "OBS returned incomplete authentication data. Regenerate the OBS WebSocket password and try again.");
            }

            string authentication = CreateAuthentication(request.Password.Span, salt, challenge);
            try
            {
                await SendJsonAsync(socket, new
                {
                    op = 1,
                    d = new
                    {
                        rpcVersion = Math.Min(serverRpcVersion, LatestSupportedRpcVersion),
                        authentication,
                        eventSubscriptions = 0,
                    },
                }, timeout.Token);
                awaitingIdentification = true;
            }
            finally
            {
                authentication = string.Empty;
            }

            using JsonDocument identified = await ReceiveJsonAsync(socket, timeout.Token);
            awaitingIdentification = false;
            if (!TryGetData(identified.RootElement, 2, out _))
            {
                return Failure(
                    ArenaErrorCodes.ObsAuthenticationFailed,
                    "OBS rejected WebSocket authentication. Verify that the credential target contains the dedicated OBS WebSocket password.");
            }

            using JsonDocument sceneCollectionResponse = await SendRequestAsync(
                socket,
                "GetSceneCollectionList",
                null,
                timeout.Token);
            string? activeSceneCollection = ReadCurrentSceneCollectionName(sceneCollectionResponse.RootElement);
            if (!string.Equals(activeSceneCollection, request.ExpectedSceneCollection, StringComparison.Ordinal))
            {
                return Failure(
                    ArenaErrorCodes.ObsSceneRequirementsMissing,
                    "OBS WebSocket authenticated, but the configured Arena scene collection is not active.");
            }

            using JsonDocument sceneListResponse = await SendRequestAsync(socket, "GetSceneList", null, timeout.Token);
            IReadOnlyList<string> sceneNames = ReadSceneNames(sceneListResponse.RootElement);
            Dictionary<string, IReadOnlyList<string>> sceneSources = new(StringComparer.Ordinal);
            foreach (string sceneName in sceneNames)
            {
                using JsonDocument sceneItemsResponse = await SendRequestAsync(
                    socket,
                    "GetSceneItemList",
                    new { sceneName },
                    timeout.Token);
                sceneSources[sceneName] = ReadSceneItemSourceNames(sceneItemsResponse.RootElement);
            }

            return new ObsWebSocketInspectionResult(
                true,
                new ObsSceneInventory(sceneSources),
                null,
                "OBS WebSocket authentication and scene inspection succeeded.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(
                ArenaErrorCodes.ObsWebSocketUnavailable,
                "OBS WebSocket did not respond within 10 seconds. Start OBS, enable its WebSocket server on loopback, and try doctor again.");
        }
        catch (WebSocketException)
        {
            return Failure(
                ArenaErrorCodes.ObsWebSocketUnavailable,
                "OBS WebSocket could not be reached. Start OBS and confirm its host and port in the local configuration.");
        }
        catch (JsonException)
        {
            return Failure(
                ArenaErrorCodes.ObsWebSocketUnavailable,
                "OBS returned an invalid WebSocket response. Confirm OBS 28 or later with WebSocket 5.x is running.");
        }
        catch (InvalidDataException) when (awaitingIdentification)
        {
            return Failure(
                ArenaErrorCodes.ObsAuthenticationFailed,
                "OBS rejected WebSocket authentication. Verify that the credential target contains the dedicated OBS WebSocket password.");
        }
        catch (InvalidDataException)
        {
            return Failure(
                ArenaErrorCodes.ObsWebSocketUnavailable,
                "OBS returned an unsupported WebSocket response. Confirm OBS 28 or later with WebSocket 5.x is running.");
        }
    }

    private static ObsWebSocketInspectionResult Failure(string code, string message) =>
        new(false, null, code, message);

    private static Uri CreateEndpoint(string host, int port)
    {
        string address = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        return new Uri($"ws://{address}:{port}/", UriKind.Absolute);
    }

    private static bool TryGetData(JsonElement message, int expectedOperation, out JsonElement data)
    {
        data = default;
        return message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("op", out JsonElement operation) &&
            operation.TryGetInt32(out int operationCode) &&
            operationCode == expectedOperation &&
            message.TryGetProperty("d", out data) &&
            data.ValueKind == JsonValueKind.Object;
    }

    private static async Task<JsonDocument> SendRequestAsync(
        ClientWebSocket socket,
        string requestType,
        object? requestData,
        CancellationToken cancellationToken)
    {
        string requestId = Guid.NewGuid().ToString("N");
        await SendJsonAsync(socket, new
        {
            op = 6,
            d = new
            {
                requestType,
                requestId,
                requestData,
            },
        }, cancellationToken);

        using JsonDocument response = await ReceiveJsonAsync(socket, cancellationToken);
        if (!TryGetData(response.RootElement, 7, out JsonElement data) ||
            !data.TryGetProperty("requestId", out JsonElement responseId) ||
            !string.Equals(responseId.GetString(), requestId, StringComparison.Ordinal) ||
            !data.TryGetProperty("requestStatus", out JsonElement status) ||
            !status.TryGetProperty("result", out JsonElement result) ||
            result.ValueKind != JsonValueKind.True)
        {
            throw new InvalidDataException("OBS request failed.");
        }

        return JsonDocument.Parse(response.RootElement.GetRawText());
    }

    private static string[] ReadSceneNames(JsonElement response)
    {
        if (!TryGetData(response, 7, out JsonElement data) ||
            !data.TryGetProperty("responseData", out JsonElement responseData) ||
            !responseData.TryGetProperty("scenes", out JsonElement scenes) ||
            scenes.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("OBS scene list response is invalid.");
        }

        return scenes
            .EnumerateArray()
            .Where(scene => scene.TryGetProperty("sceneName", out JsonElement name) && name.ValueKind == JsonValueKind.String)
            .Select(scene => scene.GetProperty("sceneName").GetString())
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string? ReadCurrentSceneCollectionName(JsonElement response)
    {
        if (!TryGetData(response, 7, out JsonElement data) ||
            !data.TryGetProperty("responseData", out JsonElement responseData) ||
            !responseData.TryGetProperty("currentSceneCollectionName", out JsonElement collectionName) ||
            collectionName.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("OBS scene collection response is invalid.");
        }

        return collectionName.GetString();
    }

    private static string[] ReadSceneItemSourceNames(JsonElement response)
    {
        if (!TryGetData(response, 7, out JsonElement data) ||
            !data.TryGetProperty("responseData", out JsonElement responseData) ||
            !responseData.TryGetProperty("sceneItems", out JsonElement sceneItems) ||
            sceneItems.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("OBS scene item response is invalid.");
        }

        return sceneItems
            .EnumerateArray()
            .Where(item => item.TryGetProperty("sourceName", out JsonElement name) && name.ValueKind == JsonValueKind.String)
            .Select(item => item.GetProperty("sourceName").GetString())
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task SendJsonAsync(
        ClientWebSocket socket,
        object message,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using MemoryStream contents = new();
            while (true)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidDataException("OBS closed the WebSocket.");
                }

                if (result.MessageType != WebSocketMessageType.Text || contents.Length + result.Count > MaximumMessageBytes)
                {
                    throw new InvalidDataException("OBS sent an unsupported WebSocket message.");
                }

                contents.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return JsonDocument.Parse(contents.ToArray());
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, true);
        }
    }

    private static string CreateAuthentication(
        ReadOnlySpan<byte> password,
        string salt,
        string challenge)
    {
        byte[] saltBytes = Encoding.UTF8.GetBytes(salt);
        byte[] challengeBytes = Encoding.UTF8.GetBytes(challenge);
        byte[] passwordAndSalt = new byte[password.Length + saltBytes.Length];
        byte[]? firstHash = null;
        byte[]? base64Secret = null;
        byte[]? secretAndChallenge = null;
        try
        {
            password.CopyTo(passwordAndSalt);
            saltBytes.CopyTo(passwordAndSalt, password.Length);
            firstHash = SHA256.HashData(passwordAndSalt);
            base64Secret = Encoding.ASCII.GetBytes(Convert.ToBase64String(firstHash));
            secretAndChallenge = new byte[base64Secret.Length + challengeBytes.Length];
            base64Secret.CopyTo(secretAndChallenge, 0);
            challengeBytes.CopyTo(secretAndChallenge, base64Secret.Length);
            byte[] authenticationHash = SHA256.HashData(secretAndChallenge);
            try
            {
                return Convert.ToBase64String(authenticationHash);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(authenticationHash);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(saltBytes);
            CryptographicOperations.ZeroMemory(challengeBytes);
            CryptographicOperations.ZeroMemory(passwordAndSalt);
            if (firstHash is not null)
            {
                CryptographicOperations.ZeroMemory(firstHash);
            }

            if (base64Secret is not null)
            {
                CryptographicOperations.ZeroMemory(base64Secret);
            }

            if (secretAndChallenge is not null)
            {
                CryptographicOperations.ZeroMemory(secretAndChallenge);
            }
        }
    }
}
