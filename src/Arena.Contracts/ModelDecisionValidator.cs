using System.Text.Json;

namespace OpenTtd.ModelArena.Contracts;

public sealed record ModelDecisionValidationResult(
    bool IsValid,
    ModelDecision? Decision,
    ArenaError? Error)
{
    public static ModelDecisionValidationResult Valid(ModelDecision decision) => new(true, decision, null);

    public static ModelDecisionValidationResult SchemaMismatch(string detail) =>
        new(
            false,
            null,
            new ArenaError(
                ArenaErrorCodes.ProviderSchemaMismatch,
                "The provider response did not satisfy the required public decision contract.",
                detail,
                true));

    public static ModelDecisionValidationResult InvalidJson(string detail) =>
        new(
            false,
            null,
            new ArenaError(
                ArenaErrorCodes.ProviderInvalidJson,
                "The provider response was not valid JSON.",
                detail,
                true));
}

/// <summary>
/// Strictly parses the common model-decision contract. It deliberately rejects
/// unknown fields such as private reasoning before a provider response reaches
/// any game-side authorization path.
/// </summary>
public static class ModelDecisionValidator
{
    public static ModelDecisionValidationResult ParseAndValidate(
        string? json,
        IReadOnlySet<string> allowedTools,
        int maximumActions = 8)
    {
        ArgumentNullException.ThrowIfNull(allowedTools);
        if (string.IsNullOrWhiteSpace(json) || json.Length > 32 * 1024)
        {
            return ModelDecisionValidationResult.SchemaMismatch("The provider response is empty or exceeds the decision size limit.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return ParseAndValidate(document.RootElement, allowedTools, maximumActions);
        }
        catch (JsonException)
        {
            return ModelDecisionValidationResult.InvalidJson("The provider response is not valid JSON.");
        }
    }

    public static ModelDecisionValidationResult ParseAndValidate(
        JsonElement root,
        IReadOnlySet<string> allowedTools,
        int maximumActions = 8)
    {
        ArgumentNullException.ThrowIfNull(allowedTools);
        if (maximumActions is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumActions), "The maximum action count must be between one and eight.");
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !HasOnlyFields(root, "decision_id", "public_summary", "observations", "actions", "next_review_game_days"))
        {
            return ModelDecisionValidationResult.SchemaMismatch("The decision must be a closed JSON object with the v1 fields.");
        }

        if (!TryGetString(root, "decision_id", 1, 128, out string decisionId) ||
            !ProtocolEnvelopeValidator.IsIdentifier(decisionId) ||
            !TryGetPublicText(root, "public_summary", 500, out string publicSummary) ||
            !root.TryGetProperty("observations", out JsonElement observationsElement) ||
            !root.TryGetProperty("actions", out JsonElement actionsElement) ||
            !root.TryGetProperty("next_review_game_days", out JsonElement reviewElement) ||
            reviewElement.ValueKind != JsonValueKind.Number ||
            !reviewElement.TryGetInt32(out int nextReviewGameDays) ||
            nextReviewGameDays is < 1 or > 365)
        {
            return ModelDecisionValidationResult.SchemaMismatch("A required decision field is missing, incorrectly typed, or outside its safe bound.");
        }

        if (observationsElement.ValueKind != JsonValueKind.Array ||
            observationsElement.GetArrayLength() is < 1 or > 8 ||
            actionsElement.ValueKind != JsonValueKind.Array ||
            actionsElement.GetArrayLength() < 1 ||
            actionsElement.GetArrayLength() > maximumActions)
        {
            return ModelDecisionValidationResult.SchemaMismatch("Decision observations and actions must be non-empty and within the declared action limit.");
        }

        List<string> observations = [];
        foreach (JsonElement observation in observationsElement.EnumerateArray())
        {
            if (observation.ValueKind != JsonValueKind.String ||
                !IsPublicText(observation.GetString(), 280))
            {
                return ModelDecisionValidationResult.SchemaMismatch("A decision observation is not bounded publication-safe text.");
            }

            observations.Add(PublicTextSanitizer.Sanitize(observation.GetString(), 280, "Observation recorded."));
        }

        List<ModelAction> actions = [];
        foreach (JsonElement action in actionsElement.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object ||
                !HasOnlyFields(action, "tool", "arguments") ||
            !TryGetString(action, "tool", 1, 80, out string tool) ||
                !RoadToolCatalog.IsToolIdentifier(tool) ||
                !allowedTools.Contains(tool) ||
                !action.TryGetProperty("arguments", out JsonElement arguments) ||
                arguments.ValueKind != JsonValueKind.Object ||
                !JsonValueBounds.IsWithinBounds(arguments))
            {
                return ModelDecisionValidationResult.SchemaMismatch("A model action is outside the declared tool allowlist or JSON bounds.");
            }

            actions.Add(new ModelAction
            {
                Tool = tool,
                Arguments = arguments.Clone(),
            });
        }

        return ModelDecisionValidationResult.Valid(new ModelDecision
        {
            DecisionId = decisionId,
            PublicSummary = PublicTextSanitizer.Sanitize(publicSummary, 500, "Decision recorded."),
            Observations = observations,
            Actions = actions,
            NextReviewGameDays = nextReviewGameDays,
        });
    }

    private static bool HasOnlyFields(JsonElement value, params string[] expectedFields)
    {
        HashSet<string> expected = new(expectedFields, StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return seen.Count == expected.Count;
    }

    private static bool TryGetString(JsonElement value, string propertyName, int minimumLength, int maximumLength, out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? candidate = property.GetString();
        if (candidate is null || candidate.Length < minimumLength || candidate.Length > maximumLength)
        {
            return false;
        }

        result = candidate;
        return true;
    }

    private static bool TryGetPublicText(JsonElement value, string propertyName, int maximumLength, out string result) =>
        TryGetString(value, propertyName, 1, maximumLength, out result) && IsPublicText(result, maximumLength);

    private static bool IsPublicText(string? value, int maximumLength) =>
        value is { Length: > 0 } && value.Length <= maximumLength && value.All(character => !char.IsControl(character));
}

public static class JsonValueBounds
{
    public const int MaximumObjectProperties = 32;
    public const int MaximumArrayItems = 64;
    public const int MaximumDepth = 8;
    public const int MaximumStringLength = 1000;

    public static bool IsWithinBounds(JsonElement value, int depth = 0)
    {
        if (depth > MaximumDepth)
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Count() <= MaximumObjectProperties &&
                value.EnumerateObject().All(property =>
                    property.Name.Length <= 80 &&
                    IsWithinBounds(property.Value, depth + 1)),
            JsonValueKind.Array => value.GetArrayLength() <= MaximumArrayItems &&
                value.EnumerateArray().All(item => IsWithinBounds(item, depth + 1)),
            JsonValueKind.String => value.GetString() is { Length: <= MaximumStringLength },
            JsonValueKind.Number => value.TryGetInt64(out _),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => false,
        };
    }
}
