using System.Net;
using System.Text;
using System.Text.Json;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Providers;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class DeepSeekModelProviderTests
{
    [Fact]
    public async Task SendsTheCommonDecisionRequestAndParsesAValidResponse()
    {
        RecordingHandler handler = new(_ => ValidDecisionResponse());
        using HttpClient client = new(handler);
        DeepSeekModelProvider provider = CreateProvider(client);

        ProviderDecisionResult result = await provider.GetDecisionAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("decision-0001", result.Decision?.DecisionId);
        Assert.Equal(12, result.Usage.InputTokens);
        Assert.Equal(7, result.Usage.OutputTokens);
        Assert.Equal("chatcmpl-sanitized-0001", result.Usage.ProviderRequestId);
        string requestBody = Assert.Single(handler.RequestBodies);
        using JsonDocument document = JsonDocument.Parse(requestBody);
        Assert.Equal("deepseek-chat", document.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_object", document.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        string userMessage = document.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        using JsonDocument prompt = JsonDocument.Parse(userMessage);
        Assert.Equal("decision-0001", prompt.RootElement.GetProperty("decision_id").GetString());
        Assert.Equal(RoadToolPromptCatalog.Version, prompt.RootElement.GetProperty("tool_contract_version").GetString());
        Assert.Equal(8, prompt.RootElement.GetProperty("maximum_actions").GetInt32());
        JsonElement waitContract = prompt.RootElement.GetProperty("tool_contracts").GetProperty("wait");
        Assert.Equal("Advance to the next review interval without construction.", waitContract.GetProperty("description").GetString());
        Assert.Equal("game_days", waitContract.GetProperty("arguments")[0].GetProperty("name").GetString());
        Assert.False(requestBody.Contains("fixture credential", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ArenaErrorCodes.ProviderAuthenticationFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, ArenaErrorCodes.ProviderRateLimited)]
    public async Task ClassifiesAuthenticationAndRateLimitFailures(HttpStatusCode status, string expectedCode)
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(status));
        using HttpClient client = new(handler);
        DeepSeekModelProvider provider = CreateProvider(client, maximumTransientRetries: 0);

        ProviderDecisionResult result = await provider.GetDecisionAsync(CreateRequest(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedCode, result.Error?.Code);
        Assert.Single(handler.RequestBodies);
    }

    [Fact]
    public async Task ClassifiesAnInvalidStructuredDecisionWithoutKeepingTheProviderBody()
    {
        RecordingHandler handler = new(_ => JsonResponse("""
            { "choices": [{ "message": { "content": "{\"decision_id\":\"decision-0001\"}" } }] }
            """));
        using HttpClient client = new(handler);
        DeepSeekModelProvider provider = CreateProvider(client);

        ProviderDecisionResult result = await provider.GetDecisionAsync(CreateRequest(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ArenaErrorCodes.ProviderSchemaMismatch, result.Error?.Code);
        Assert.DoesNotContain("decision-0001", result.Error?.TechnicalContext ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClassifiesMalformedProviderEnvelopeAsInvalidJson()
    {
        RecordingHandler handler = new(_ => JsonResponse("{not-json}"));
        using HttpClient client = new(handler);
        DeepSeekModelProvider provider = CreateProvider(client);

        ProviderDecisionResult result = await provider.GetDecisionAsync(CreateRequest(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ArenaErrorCodes.ProviderInvalidJson, result.Error?.Code);
    }

    [Fact]
    public async Task RetriesOneTransientServerFailureBeforeAcceptingTheDecision()
    {
        int requests = 0;
        RecordingHandler handler = new(_ =>
        {
            requests++;
            return requests == 1
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : ValidDecisionResponse();
        });
        using HttpClient client = new(handler);
        DeepSeekModelProvider provider = CreateProvider(client, maximumTransientRetries: 1);

        ProviderDecisionResult result = await provider.GetDecisionAsync(CreateRequest(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task ClassifiesAProviderTimeoutSeparately()
    {
        RecordingHandler handler = new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using HttpClient client = new(handler);
        DeepSeekModelProvider provider = CreateProvider(client, timeout: TimeSpan.FromSeconds(1));

        ProviderDecisionResult result = await provider.GetDecisionAsync(CreateRequest(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ArenaErrorCodes.ProviderTimeout, result.Error?.Code);
    }

    [Fact]
    public async Task ClassifiesCallerCancellationSeparately()
    {
        RecordingHandler handler = new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using HttpClient client = new(handler);
        DeepSeekModelProvider provider = CreateProvider(client);
        using CancellationTokenSource cancellation = new();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        ProviderDecisionResult result = await provider.GetDecisionAsync(CreateRequest(), cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(ArenaErrorCodes.ProviderCancelled, result.Error?.Code);
    }

    private static DeepSeekModelProvider CreateProvider(
        HttpClient client,
        int maximumTransientRetries = 0,
        TimeSpan? timeout = null) =>
        new(
            client,
            new FixtureCredentialResolver(),
            new DeepSeekProviderOptions(
                new Uri("https://provider.invalid/"),
                "deepseek-chat",
                timeout ?? TimeSpan.FromSeconds(2),
                maximumTransientRetries));

    private static ModelRequest CreateRequest()
    {
        using JsonDocument observation = JsonDocument.Parse("""
            { "schema_version": "1.0", "sections": {} }
            """);
        return new ModelRequest
        {
            RunId = "run-0001",
            DecisionId = "decision-0001",
            ObservationHash = new string('a', 64),
            Observation = observation.RootElement.Clone(),
            AvailableTools = ["wait"],
            RemainingModelCalls = 1,
            RemainingOutputTokens = 100,
            PromptTemplateVersion = ArenaPromptTemplate.Version,
            PromptTemplateSha256 = ArenaPromptTemplate.Sha256,
        };
    }

    private static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage ValidDecisionResponse() => JsonResponse(File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "fixtures",
        "providers",
        "deepseek-chat-completion.v1.sanitized.json")));

    private sealed class FixtureCredentialResolver : IProviderCredentialResolver
    {
        public Task<ProviderCredentialResolution> ResolveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ProviderCredentialResolution.Success(SecretMaterial.FromUtf8("fixture credential")));
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : this((request, _) => Task.FromResult(send(request)))
        {
        }

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return await _send(request, cancellationToken);
        }
    }
}
