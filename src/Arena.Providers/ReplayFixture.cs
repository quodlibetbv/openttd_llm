using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Providers;

public sealed record ReplayFixture
{
    [JsonPropertyName("fixture_version")]
    public required string FixtureVersion { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("steps")]
    public required IReadOnlyList<ReplayStep> Steps { get; init; }
}

public sealed record ReplayStep
{
    [JsonPropertyName("expected_observation_sha256")]
    public required string ExpectedObservationSha256 { get; init; }

    [JsonPropertyName("decision")]
    public required ModelDecision Decision { get; init; }

    [JsonPropertyName("usage")]
    public required ReplayUsage Usage { get; init; }
}

public sealed record ReplayUsage
{
    [JsonPropertyName("input_tokens")]
    public required long InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public required long OutputTokens { get; init; }

    [JsonPropertyName("latency_ms")]
    public required long LatencyMilliseconds { get; init; }
}

public static class ReplayFixtureReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    public static ReplayFixture Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        ReplayFixture? fixture = JsonSerializer.Deserialize<ReplayFixture>(stream, SerializerOptions);
        if (fixture is null || fixture.Steps.Count == 0)
        {
            throw new JsonException("Replay fixtures must contain at least one sanitized step.");
        }

        return fixture;
    }
}
