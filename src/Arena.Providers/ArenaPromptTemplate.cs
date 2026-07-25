using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Providers;

/// <summary>
/// Versioned, provider-neutral prompt framing. The observation and tool
/// allowlist are passed as canonical JSON, while hidden reasoning is neither
/// requested nor represented in the common decision contract.
/// </summary>
public static class ArenaPromptTemplate
{
    public const string Version = "1.4";

    private const string SystemTemplate = """
You are a strategic transport benchmark participant. Return exactly one JSON object matching the Arena model-decision.v1 contract. The object must contain decision_id, public_summary, observations, actions, and next_review_game_days. Copy the supplied decision_id exactly. Use only the declared tools and their typed arguments. Public text must be concise and publishable. Do not provide hidden reasoning, chain-of-thought, markdown, or any field outside the JSON contract.
""";

    private const string SchemaCorrectionInstruction =
        "The previous response did not satisfy the schema. Return a corrected JSON object only; do not explain the correction.";

    private const string UserPayloadContract =
        "decision_id,observation_sha256,observation,available_tools,tool_contract_version,tool_contracts,remaining_model_calls,remaining_output_tokens,maximum_actions,response_contract=model-decision.v1 json";

    private static readonly string ToolContractsCanonicalJson = CanonicalJson.SerializeToString(
        JsonSerializer.SerializeToElement(RoadToolPromptCatalog.AllContracts));

    public static string Sha256 { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(
            SystemTemplate + "\n" + RoadToolPromptCatalog.Version + "\n" +
            ToolContractsCanonicalJson + "\n" + SchemaCorrectionInstruction + "\n" +
            UserPayloadContract))).ToLowerInvariant();

    public static string CreateSystemMessage(ModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.SchemaCorrectionAttempt == 0
            ? SystemTemplate
            : SystemTemplate + "\n" + SchemaCorrectionInstruction;
    }

    public static string CreateUserMessage(ModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[] observation = CanonicalJson.Serialize(request.Observation);
        using JsonDocument document = JsonDocument.Parse(observation);
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            decision_id = request.DecisionId,
            observation_sha256 = request.ObservationHash,
            observation = document.RootElement.Clone(),
            available_tools = request.AvailableTools.OrderBy(tool => tool, StringComparer.Ordinal).ToArray(),
            tool_contract_version = RoadToolPromptCatalog.Version,
            tool_contracts = RoadToolPromptCatalog.CreateAllowedContracts(request.AvailableTools),
            remaining_model_calls = request.RemainingModelCalls,
            remaining_output_tokens = request.RemainingOutputTokens,
            maximum_actions = request.MaximumActions,
            response_contract = "model-decision.v1 json",
        });
        return CanonicalJson.SerializeToString(payload);
    }
}
