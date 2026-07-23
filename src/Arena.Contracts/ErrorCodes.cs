namespace OpenTtd.ModelArena.Contracts;

public static class ArenaErrorCodes
{
    public const string ProviderTimeout = "ARENA-PROVIDER-TIMEOUT";
    public const string ProviderInvalidOutput = "ARENA-PROVIDER-INVALID-OUTPUT";
    public const string ProviderReplayExhausted = "ARENA-PROVIDER-REPLAY-EXHAUSTED";
    public const string ProviderReplayObservationMismatch = "ARENA-PROVIDER-REPLAY-OBSERVATION-MISMATCH";
    public const string ProtocolVersionMismatch = "ARENA-PROTOCOL-VERSION-MISMATCH";
    public const string ProtocolInvalidMessage = "ARENA-PROTOCOL-INVALID-MESSAGE";
    public const string ActionConstraintViolation = "ARENA-ACTION-CONSTRAINT-VIOLATION";
    public const string ActionPathNotFound = "ARENA-ACTION-PATH-NOT-FOUND";
    public const string OpenTtdProcessExited = "ARENA-OPENTTD-PROCESS-EXITED";
    public const string ObsRecordingFailed = "ARENA-OBS-RECORDING-FAILED";
    public const string ArtifactVerificationFailed = "ARENA-ARTIFACT-VERIFICATION-FAILED";
    public const string PathOutsideRunRoot = "ARENA-STORAGE-PATH-OUTSIDE-RUN-ROOT";
}

public sealed record ArenaError(
    string Code,
    string UserMessage,
    string TechnicalContext,
    bool IsRetryable);
