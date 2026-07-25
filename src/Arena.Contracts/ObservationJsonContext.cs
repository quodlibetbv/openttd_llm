using System.Text.Json.Serialization;

namespace OpenTtd.ModelArena.Contracts;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = false)]
[JsonSerializable(typeof(GameScriptSnapshot))]
[JsonSerializable(typeof(ObservationSnapshot))]
[JsonSerializable(typeof(ObservationDelta))]
[JsonSerializable(typeof(ObservationDeltaOperation))]
[JsonSerializable(typeof(NormalizedGameEvent))]
[JsonSerializable(typeof(ModelDecision))]
[JsonSerializable(typeof(ActionRequest))]
[JsonSerializable(typeof(ActionResult))]
public partial class ObservationJsonContext : JsonSerializerContext
{
}
