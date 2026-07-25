using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Providers;

public sealed record DeepSeekProviderOptions(
    Uri BaseUri,
    string Model,
    TimeSpan Timeout,
    int MaximumTransientRetries,
    decimal? InputCostPerMillionTokens = null,
    decimal? OutputCostPerMillionTokens = null,
    string ProviderId = "deepseek")
{
    public void Validate()
    {
        if (!BaseUri.IsAbsoluteUri ||
            !string.Equals(BaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(BaseUri.UserInfo) ||
            !string.IsNullOrEmpty(BaseUri.Query) ||
            !string.IsNullOrEmpty(BaseUri.Fragment) ||
            string.IsNullOrWhiteSpace(Model) ||
            Model.Length > 160 ||
            !RoadToolCatalog.IsToolIdentifier(ProviderId) ||
            Timeout < TimeSpan.FromSeconds(1) ||
            Timeout > TimeSpan.FromMinutes(5) ||
            MaximumTransientRetries is < 0 or > 2 ||
            InputCostPerMillionTokens is < 0 ||
            OutputCostPerMillionTokens is < 0)
        {
            throw new ArgumentException("The DeepSeek provider options are outside the supported Phase 05 bounds.");
        }
    }
}

/// <summary>
/// DeepSeek's OpenAI-compatible chat-completions adapter. It asks for JSON
/// output, validates locally against the common contract, and intentionally
/// never serializes raw provider request or response bodies.
/// </summary>
public sealed class DeepSeekModelProvider : IModelProvider
{
    private const int MaximumResponseBytes = 64 * 1024;
    private readonly IProviderCredentialResolver _credentialResolver;
    private readonly HttpClient _httpClient;
    private readonly DeepSeekProviderOptions _options;
    private readonly TimeProvider _timeProvider;

    public DeepSeekModelProvider(
        HttpClient httpClient,
        IProviderCredentialResolver credentialResolver,
        DeepSeekProviderOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentialResolver);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _httpClient = httpClient;
        _credentialResolver = credentialResolver;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Descriptor = new ProviderDescriptor(
            ProviderId: options.ProviderId,
            AdapterVersion: "1.0",
            SupportsStructuredOutput: true);
    }

    public ProviderDescriptor Descriptor { get; }

    public async Task<ProviderDecisionResult> GetDecisionAsync(
        ModelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Stopwatch stopwatch = Stopwatch.StartNew();
        ProviderCredentialResolution credential = await _credentialResolver.ResolveAsync(cancellationToken);
        if (!credential.Succeeded || credential.Secret is null)
        {
            return ProviderDecisionResult.Failed(
                credential.Error ?? new ArenaError(
                    ArenaErrorCodes.CredentialMissing,
                    "The configured provider credential is unavailable.",
                    "The credential resolver returned no credential material.",
                    false),
                CreateUsage(stopwatch.Elapsed, 0, 0, null));
        }

        using SecretMaterial secret = credential.Secret;
        for (int attempt = 0; attempt <= _options.MaximumTransientRetries; attempt++)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);
            try
            {
                using HttpRequestMessage message = CreateRequest(request, secret);
                using HttpResponseMessage response = await _httpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);

                if (response.IsSuccessStatusCode)
                {
                    return await ParseSuccessAsync(response, request, stopwatch.Elapsed, timeout.Token);
                }

                ArenaError error = ClassifyHttpFailure(response.StatusCode);
                if (IsRetryableStatus(response.StatusCode) && attempt < _options.MaximumTransientRetries)
                {
                    await DelayBeforeRetryAsync(response, attempt, cancellationToken);
                    continue;
                }

                return ProviderDecisionResult.Failed(error, CreateUsage(stopwatch.Elapsed, 0, 0, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ProviderDecisionResult.Failed(
                    new ArenaError(
                        ArenaErrorCodes.ProviderCancelled,
                        "The provider call was cancelled before a decision was accepted.",
                        "The caller cancelled the provider request.",
                        false),
                    CreateUsage(stopwatch.Elapsed, 0, 0, null));
            }
            catch (OperationCanceledException)
            {
                return ProviderDecisionResult.Failed(
                    new ArenaError(
                        ArenaErrorCodes.ProviderTimeout,
                        "The provider did not return a decision before its configured timeout.",
                        "The provider request timed out.",
                        true),
                    CreateUsage(stopwatch.Elapsed, 0, 0, null));
            }
            catch (HttpRequestException)
            {
                if (attempt < _options.MaximumTransientRetries)
                {
                    await DelayBeforeRetryAsync(null, attempt, cancellationToken);
                    continue;
                }

                return ProviderDecisionResult.Failed(
                    new ArenaError(
                        ArenaErrorCodes.ProviderRequestFailed,
                        "The provider request could not be completed.",
                        "The provider transport failed without exposing provider response content.",
                        true),
                    CreateUsage(stopwatch.Elapsed, 0, 0, null));
            }
        }

        throw new InvalidOperationException("The bounded provider retry loop terminated unexpectedly.");
    }

    private HttpRequestMessage CreateRequest(ModelRequest request, SecretMaterial secret)
    {
        string credential = Encoding.UTF8.GetString(secret.Bytes.Span);
        Uri endpoint = new(_options.BaseUri, "chat/completions");
        JsonElement body = JsonSerializer.SerializeToElement(new
        {
            model = _options.Model,
            messages = new[]
            {
                new { role = "system", content = ArenaPromptTemplate.CreateSystemMessage(request) },
                new { role = "user", content = ArenaPromptTemplate.CreateUserMessage(request) },
            },
            response_format = new { type = "json_object" },
            max_tokens = request.RemainingOutputTokens,
            temperature = 0,
            stream = false,
        });
        byte[] payload = CanonicalJson.Serialize(body);
        HttpRequestMessage message = new(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(payload),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return message;
    }

    private async Task<ProviderDecisionResult> ParseSuccessAsync(
        HttpResponseMessage response,
        ModelRequest request,
        TimeSpan latency,
        CancellationToken cancellationToken)
    {
        byte[]? body = await ReadBoundedBodyAsync(response.Content, cancellationToken);
        if (body is null)
        {
            return ProviderDecisionResult.Failed(
                new ArenaError(
                    ArenaErrorCodes.ProviderSchemaMismatch,
                    "The provider response exceeded the supported decision size.",
                    "The provider success response exceeded the bounded response size without retaining its body.",
                    true),
                CreateUsage(latency, 0, 0, null));
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            string? responseId = TryGetString(root, "id");
            long inputTokens = TryGetUsageTokenCount(root, "prompt_tokens");
            long outputTokens = TryGetUsageTokenCount(root, "completion_tokens");
            string? decisionJson = TryGetFirstChoiceContent(root);
            ModelDecisionValidationResult validation = ModelDecisionValidator.ParseAndValidate(
                decisionJson,
                request.AvailableTools.ToHashSet(StringComparer.Ordinal),
                request.MaximumActions);
            ProviderUsage usage = CreateUsage(latency, inputTokens, outputTokens, responseId);
            return validation.IsValid && validation.Decision is not null
                ? ProviderDecisionResult.Succeeded(validation.Decision, usage)
                : ProviderDecisionResult.Failed(validation.Error!, usage);
        }
        catch (JsonException)
        {
            return ProviderDecisionResult.Failed(
                new ArenaError(
                    ArenaErrorCodes.ProviderInvalidJson,
                    "The provider returned a response that could not be interpreted as a model decision.",
                    "The provider success response was not valid JSON without retaining its body.",
                    true),
                CreateUsage(latency, 0, 0, null));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(body);
        }
    }

    private static async Task<byte[]?> ReadBoundedBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            return null;
        }

        byte[] buffer = new byte[MaximumResponseBytes + 1];
        try
        {
            await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
            int total = 0;
            while (total <= MaximumResponseBytes)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
                if (read == 0)
                {
                    return buffer[..total].ToArray();
                }

                total += read;
                if (total > MaximumResponseBytes)
                {
                    return null;
                }
            }

            return null;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private ProviderUsage CreateUsage(TimeSpan latency, long inputTokens, long outputTokens, string? responseId)
    {
        decimal? estimatedCost = _options.InputCostPerMillionTokens is not null || _options.OutputCostPerMillionTokens is not null
            ? ((inputTokens * (_options.InputCostPerMillionTokens ?? 0m)) +
               (outputTokens * (_options.OutputCostPerMillionTokens ?? 0m))) / 1_000_000m
            : null;
        return new ProviderUsage(inputTokens, outputTokens, latency, responseId, estimatedCost);
    }

    private async Task DelayBeforeRetryAsync(HttpResponseMessage? response, int attempt, CancellationToken cancellationToken)
    {
        TimeSpan delay = response?.Headers.RetryAfter?.Delta is { } retryAfter
            ? retryAfter
            : TimeSpan.FromMilliseconds(250 * (attempt + 1));
        if (delay > TimeSpan.FromSeconds(5))
        {
            delay = TimeSpan.FromSeconds(5);
        }

        await Task.Delay(delay, _timeProvider, cancellationToken);
    }

    private static bool IsRetryableStatus(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static ArenaError ClassifyHttpFailure(HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ArenaError(
                ArenaErrorCodes.ProviderAuthenticationFailed,
                "The provider rejected the configured credential.",
                $"The provider returned HTTP {(int)statusCode} without retaining its response body.",
                false),
            HttpStatusCode.TooManyRequests => new ArenaError(
                ArenaErrorCodes.ProviderRateLimited,
                "The provider rate limit prevented a decision request.",
                "The provider returned HTTP 429 without retaining its response body.",
                true),
            _ => new ArenaError(
                ArenaErrorCodes.ProviderRequestFailed,
                "The provider could not complete the decision request.",
                $"The provider returned HTTP {(int)statusCode} without retaining its response body.",
                (int)statusCode >= 500),
        };

    private static string? TryGetFirstChoiceContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() < 1 ||
            choices[0].ValueKind != JsonValueKind.Object ||
            !choices[0].TryGetProperty("message", out JsonElement message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return content.GetString();
    }

    private static string? TryGetString(JsonElement root, string field) =>
        root.TryGetProperty(field, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long TryGetUsageTokenCount(JsonElement root, string field) =>
        root.TryGetProperty("usage", out JsonElement usage) &&
        usage.ValueKind == JsonValueKind.Object &&
        usage.TryGetProperty(field, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out long tokenCount) &&
        tokenCount >= 0
            ? tokenCount
            : 0;
}
