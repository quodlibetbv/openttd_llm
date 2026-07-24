using System.Text.Json.Serialization;

namespace OpenTtd.ModelArena.Contracts;

/// <summary>
/// The persisted lifecycle of an unattended OpenTTD process run. The values
/// deliberately match the Phase 02 state diagram and are append-only once
/// written to a run's lifecycle journal.
/// </summary>
public enum ArenaRunState
{
    Created,
    Preparing,
    StartingServer,
    WaitingForGameScript,
    StartingClients,
    Ready,
    Running,
    Finalizing,
    Completed,
    Failed,
    Cancelled,
}

public enum ArenaRunExitReason
{
    Completed,
    Cancelled,
    PreparationFailed,
    StartupTimedOut,
    GameScriptNotReady,
    ServerExited,
    SpectatorExited,
    FinalizationFailed,
}

public sealed record RunLifecycleEvent
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("occurred_utc")]
    public required DateTimeOffset OccurredUtc { get; init; }

    [JsonPropertyName("state")]
    public required ArenaRunState State { get; init; }

    [JsonPropertyName("component_id")]
    public string? ComponentId { get; init; }

    [JsonPropertyName("exit_reason")]
    public ArenaRunExitReason? ExitReason { get; init; }

    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

public sealed record RunComponentResult
{
    [JsonPropertyName("component_id")]
    public required string ComponentId { get; init; }

    [JsonPropertyName("process_id")]
    public required int ProcessId { get; init; }

    [JsonPropertyName("stable_window_title")]
    public string? StableWindowTitle { get; init; }

    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; init; }
}

public sealed record RunArtifactRecord
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("bytes")]
    public required long Bytes { get; init; }
}

public sealed record ArenaRunResult
{
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("created_utc")]
    public required DateTimeOffset CreatedUtc { get; init; }

    [JsonPropertyName("completed_utc")]
    public required DateTimeOffset CompletedUtc { get; init; }

    [JsonPropertyName("final_state")]
    public required ArenaRunState FinalState { get; init; }

    [JsonPropertyName("exit_reason")]
    public required ArenaRunExitReason ExitReason { get; init; }

    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("components")]
    public required IReadOnlyList<RunComponentResult> Components { get; init; }

    [JsonPropertyName("artifacts")]
    public required IReadOnlyList<RunArtifactRecord> Artifacts { get; init; }
}
