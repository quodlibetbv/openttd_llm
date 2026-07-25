namespace OpenTtd.ModelArena.Contracts;

public static class ArenaErrorCodes
{
    public const string ProviderTimeout = "ARENA-PROVIDER-TIMEOUT";
    public const string ProviderInvalidOutput = "ARENA-PROVIDER-INVALID-OUTPUT";
    public const string ProviderInvalidJson = "ARENA-PROVIDER-INVALID-JSON";
    public const string ProviderSchemaMismatch = "ARENA-PROVIDER-SCHEMA-MISMATCH";
    public const string ProviderAuthenticationFailed = "ARENA-PROVIDER-AUTHENTICATION-FAILED";
    public const string ProviderRateLimited = "ARENA-PROVIDER-RATE-LIMITED";
    public const string ProviderRequestFailed = "ARENA-PROVIDER-REQUEST-FAILED";
    public const string ProviderCancelled = "ARENA-PROVIDER-CANCELLED";
    public const string ProviderConfigurationInvalid = "ARENA-PROVIDER-CONFIGURATION-INVALID";
    public const string ProviderNotConfigured = "ARENA-PROVIDER-NOT-CONFIGURED";
    public const string ProviderReplayExhausted = "ARENA-PROVIDER-REPLAY-EXHAUSTED";
    public const string ProviderReplayObservationMismatch = "ARENA-PROVIDER-REPLAY-OBSERVATION-MISMATCH";
    public const string ProtocolVersionMismatch = "ARENA-PROTOCOL-VERSION-MISMATCH";
    public const string ProtocolInvalidMessage = "ARENA-PROTOCOL-INVALID-MESSAGE";
    public const string ProtocolMessageTooLarge = "ARENA-PROTOCOL-MESSAGE-TOO-LARGE";
    public const string ProtocolChunkInvalid = "ARENA-PROTOCOL-CHUNK-INVALID";
    public const string ProtocolChunkTimeout = "ARENA-PROTOCOL-CHUNK-TIMEOUT";
    public const string ProtocolStaleCorrelation = "ARENA-PROTOCOL-STALE-CORRELATION";
    public const string AdminPortUnavailable = "ARENA-ADMINPORT-UNAVAILABLE";
    public const string AdminPortAuthenticationFailed = "ARENA-ADMINPORT-AUTHENTICATION-FAILED";
    public const string AdminPortProtocolIncompatible = "ARENA-ADMINPORT-PROTOCOL-INCOMPATIBLE";
    public const string AdminPortQueueFull = "ARENA-ADMINPORT-QUEUE-FULL";
    public const string AdminPortRequestTimedOut = "ARENA-ADMINPORT-REQUEST-TIMED-OUT";
    public const string AdminPortReconnectFailed = "ARENA-ADMINPORT-RECONNECT-FAILED";
    public const string AdminPortSecretInvalid = "ARENA-ADMINPORT-SECRET-INVALID";
    public const string ActionConstraintViolation = "ARENA-ACTION-CONSTRAINT-VIOLATION";
    public const string ActionPathNotFound = "ARENA-ACTION-PATH-NOT-FOUND";
    public const string ActionStationPlacementFailed = "ARENA-ACTION-STATION-PLACEMENT-FAILED";
    public const string ActionBudgetExceeded = "ARENA-ACTION-BUDGET-EXCEEDED";
    public const string ActionInsufficientFunds = "ARENA-ACTION-INSUFFICIENT-FUNDS";
    public const string ActionVehicleUnsuitable = "ARENA-ACTION-VEHICLE-UNSUITABLE";
    public const string ActionOrderInvalid = "ARENA-ACTION-ORDER-INVALID";
    public const string ActionVerificationTimedOut = "ARENA-ACTION-VERIFICATION-TIMED-OUT";
    public const string OpenTtdProcessExited = "ARENA-OPENTTD-PROCESS-EXITED";
    public const string ObsRecordingFailed = "ARENA-OBS-RECORDING-FAILED";
    public const string ArtifactVerificationFailed = "ARENA-ARTIFACT-VERIFICATION-FAILED";
    public const string PathOutsideRunRoot = "ARENA-STORAGE-PATH-OUTSIDE-RUN-ROOT";
    public const string ConfigurationInvalid = "ARENA-CONFIG-INVALID";
    public const string ConfigurationSecretDetected = "ARENA-CONFIG-SECRET-DETECTED";
    public const string CredentialReferenceInvalid = "ARENA-CREDENTIAL-REFERENCE-INVALID";
    public const string CredentialMissing = "ARENA-CREDENTIAL-MISSING";
    public const string CredentialStoreUnavailable = "ARENA-CREDENTIAL-STORE-UNAVAILABLE";
    public const string RuntimeLayoutInvalid = "ARENA-RUNTIME-LAYOUT-INVALID";
    public const string DoctorPrerequisiteFailed = "ARENA-DOCTOR-PREREQUISITE-FAILED";
    public const string DoctorCheckPassed = "ARENA-DOCTOR-CHECK-PASSED";
    public const string DoctorDeferred = "ARENA-DOCTOR-DEFERRED";
    public const string DoctorPathNotWritable = "ARENA-DOCTOR-PATH-NOT-WRITABLE";
    public const string DoctorPortUnavailable = "ARENA-DOCTOR-PORT-UNAVAILABLE";
    public const string DoctorDiskSpaceLow = "ARENA-DOCTOR-DISK-SPACE-LOW";
    public const string ObsTemplateInvalid = "ARENA-OBS-TEMPLATE-INVALID";
    public const string ObsWebSocketUnavailable = "ARENA-OBS-WEBSOCKET-UNAVAILABLE";
    public const string ObsAuthenticationFailed = "ARENA-OBS-AUTHENTICATION-FAILED";
    public const string ObsSceneRequirementsMissing = "ARENA-OBS-SCENE-REQUIREMENTS-MISSING";
    public const string RunAllocationFailed = "ARENA-RUN-ALLOCATION-FAILED";
    public const string RunPreparationFailed = "ARENA-RUN-PREPARATION-FAILED";
    public const string RunStartupTimedOut = "ARENA-RUN-STARTUP-TIMED-OUT";
    public const string RunGameScriptNotReady = "ARENA-RUN-GAMESCRIPT-NOT-READY";
    public const string RunServerExited = "ARENA-RUN-SERVER-EXITED";
    public const string RunSpectatorExited = "ARENA-RUN-SPECTATOR-EXITED";
    public const string RunCancelled = "ARENA-RUN-CANCELLED";
    public const string RunFinalizationFailed = "ARENA-RUN-FINALIZATION-FAILED";
    public const string RunConsoleControlFailed = "ARENA-RUN-CONSOLE-CONTROL-FAILED";
    public const string RunArtifactMissing = "ARENA-RUN-ARTIFACT-MISSING";
}

public sealed record ArenaError(
    string Code,
    string UserMessage,
    string TechnicalContext,
    bool IsRetryable);
