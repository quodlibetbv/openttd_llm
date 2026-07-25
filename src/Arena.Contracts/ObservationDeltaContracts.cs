using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text;

namespace OpenTtd.ModelArena.Contracts;

/// <summary>
/// Versioned replacement/remove operations between two canonical public
/// observations. Arrays are replaced as a whole so their deterministic order
/// remains part of the observation contract rather than a hidden patching
/// convention.
/// </summary>
public sealed record ObservationDelta
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("from_observation_sha256")]
    public required string FromObservationSha256 { get; init; }

    [JsonPropertyName("to_observation_sha256")]
    public required string ToObservationSha256 { get; init; }

    [JsonPropertyName("operations")]
    public required IReadOnlyList<ObservationDeltaOperation> Operations { get; init; }
}

public sealed record ObservationDeltaOperation
{
    public const string Set = "set";
    public const string Remove = "remove";

    [JsonPropertyName("op")]
    public required string Operation { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Value { get; init; }
}

public sealed record ObservationDeltaApplyResult(JsonElement? Observation, ArenaError? Error)
{
    public bool Succeeded => Observation is not null && Error is null;
}

/// <summary>
/// Produces and applies deterministic JSON-pointer deltas for public Arena
/// observations. A delta always carries the hashes of both endpoints, so a
/// stale or partially loaded snapshot cannot silently accept an update.
/// </summary>
public static class ObservationDeltaCodec
{
    private const int MaximumOperations = 128;

    public static ObservationDelta Create(string runId, JsonElement previous, JsonElement current)
    {
        if (!ProtocolEnvelopeValidator.IsIdentifier(runId))
        {
            throw new ArgumentException("The observation-delta run identifier is invalid.", nameof(runId));
        }

        if (previous.ValueKind != JsonValueKind.Object || current.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Observation deltas require object roots.");
        }

        List<ObservationDeltaOperation> operations = [];
        BuildOperations(previous, current, string.Empty, operations);
        if (operations.Count > MaximumOperations)
        {
            throw new ArgumentException("The observation change exceeds the bounded v1 delta operation limit.");
        }

        return new ObservationDelta
        {
            SchemaVersion = ContractVersions.ObservationV1,
            RunId = runId,
            FromObservationSha256 = ComputeHash(previous),
            ToObservationSha256 = ComputeHash(current),
            Operations = operations,
        };
    }

    public static ObservationDeltaApplyResult Apply(JsonElement previous, ObservationDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (previous.ValueKind != JsonValueKind.Object ||
            !ProtocolEnvelopeValidator.IsIdentifier(delta.RunId) ||
            !string.Equals(delta.SchemaVersion, ContractVersions.ObservationV1, StringComparison.Ordinal) ||
            delta.Operations.Count > MaximumOperations)
        {
            return Failure("The observation delta is outside the v1 contract bounds.");
        }

        if (!string.Equals(ComputeHash(previous), delta.FromObservationSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Failure("The observation delta does not apply to this recorded base snapshot.");
        }

        JsonNode? rootNode = JsonNode.Parse(previous.GetRawText());
        if (rootNode is not JsonObject root)
        {
            return Failure("The recorded base observation could not be materialized for a bounded delta.");
        }

        foreach (ObservationDeltaOperation operation in delta.Operations)
        {
            if (!ApplyOperation(root, operation, out string? error))
            {
                return Failure(error ?? "The observation delta contains an invalid operation.");
            }
        }

        try
        {
            using JsonDocument resultDocument = JsonDocument.Parse(root.ToJsonString());
            JsonElement result = resultDocument.RootElement.Clone();
            if (!string.Equals(ComputeHash(result), delta.ToObservationSha256, StringComparison.OrdinalIgnoreCase))
            {
                return Failure("The observation delta did not reconstruct its declared target snapshot.");
            }

            return new ObservationDeltaApplyResult(result, null);
        }
        catch (JsonException)
        {
            return Failure("The observation delta produced invalid JSON.");
        }
    }

    public static string ComputeHash(JsonElement observation) =>
        CanonicalJson.ComputeSha256(CanonicalJson.Serialize(observation));

    private static void BuildOperations(
        JsonElement previous,
        JsonElement current,
        string path,
        List<ObservationDeltaOperation> operations)
    {
        if (previous.ValueKind == JsonValueKind.Object && current.ValueKind == JsonValueKind.Object)
        {
            Dictionary<string, JsonElement> previousProperties = previous.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            Dictionary<string, JsonElement> currentProperties = current.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            foreach (string name in previousProperties.Keys.Union(currentProperties.Keys, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal))
            {
                string childPath = path + "/" + EscapePointerSegment(name);
                bool wasPresent = previousProperties.TryGetValue(name, out JsonElement previousValue);
                bool isPresent = currentProperties.TryGetValue(name, out JsonElement currentValue);
                if (!wasPresent)
                {
                    operations.Add(SetOperation(childPath, currentValue));
                }
                else if (!isPresent)
                {
                    operations.Add(new ObservationDeltaOperation
                    {
                        Operation = ObservationDeltaOperation.Remove,
                        Path = childPath,
                    });
                }
                else
                {
                    BuildOperations(previousValue, currentValue, childPath, operations);
                }
            }

            return;
        }

        if (!CanonicalJson.Serialize(previous).AsSpan().SequenceEqual(CanonicalJson.Serialize(current)))
        {
            operations.Add(SetOperation(path, current));
        }
    }

    private static ObservationDeltaOperation SetOperation(string path, JsonElement value) => new()
    {
        Operation = ObservationDeltaOperation.Set,
        Path = path,
        Value = value.Clone(),
    };

    private static bool ApplyOperation(JsonObject root, ObservationDeltaOperation operation, out string? error)
    {
        error = null;
        if (operation is null)
        {
            error = "The observation delta operation is missing.";
            return false;
        }

        string path = operation.Path;
        if ((operation.Operation != ObservationDeltaOperation.Set && operation.Operation != ObservationDeltaOperation.Remove) ||
            string.IsNullOrEmpty(path) ||
            path[0] != '/' ||
            (operation.Operation == ObservationDeltaOperation.Set && operation.Value is null) ||
            (operation.Operation == ObservationDeltaOperation.Remove && operation.Value is not null))
        {
            error = "The observation delta operation has an invalid type, path, or value shape.";
            return false;
        }

        string?[] segments = path.Split('/').Skip(1).Select(UnescapePointerSegment).ToArray();
        if (segments.Length == 0 || segments.Any(segment => segment is null || segment.Length == 0))
        {
            error = "The observation delta operation has an invalid JSON-pointer path.";
            return false;
        }

        JsonObject parent = root;
        for (int index = 0; index < segments.Length - 1; index++)
        {
            string segment = segments[index]!;
            if (!parent.TryGetPropertyValue(segment, out JsonNode? child) || child is not JsonObject childObject)
            {
                error = "The observation delta operation does not target an existing public object path.";
                return false;
            }

            parent = childObject;
        }

        string leaf = segments[^1]!;
        if (operation.Operation == ObservationDeltaOperation.Remove)
        {
            if (!parent.Remove(leaf))
            {
                error = "The observation delta attempted to remove an absent public field.";
                return false;
            }

            return true;
        }

        try
        {
            parent[leaf] = JsonNode.Parse(operation.Value!.Value.GetRawText());
            return true;
        }
        catch (JsonException)
        {
            error = "The observation delta set value is not valid JSON.";
            return false;
        }
    }

    private static string EscapePointerSegment(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string? UnescapePointerSegment(string value)
    {
        StringBuilder result = new();
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character != '~')
            {
                result.Append(character);
                continue;
            }

            if (index + 1 >= value.Length || (value[index + 1] != '0' && value[index + 1] != '1'))
            {
                return null;
            }

            result.Append(value[index + 1] == '0' ? '~' : '/');
            index++;
        }

        return result.ToString();
    }

    private static ObservationDeltaApplyResult Failure(string message) => new(
        null,
        new ArenaError(
            ArenaErrorCodes.ArtifactVerificationFailed,
            message,
            "A recorded observation delta failed deterministic integrity verification.",
            false));
}
