namespace OpenTtd.ModelArena.Contracts;

public static class ArenaLogFields
{
    public const string RunId = "run_id";
    public const string DecisionId = "decision_id";
    public const string ActionId = "action_id";
    public const string GameDate = "game_date";
    public const string CorrelationId = "correlation_id";
    public const string ErrorCode = "error_code";
    public const string Component = "component";
    public const string EventName = "event_name";

    public static IReadOnlyList<string> RequiredRunScopeFields { get; } =
    [
        RunId,
        CorrelationId,
        Component,
        EventName,
    ];
}
