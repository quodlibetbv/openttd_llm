using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Orchestrator;

/// <summary>
/// Real-process Phase 04 proof. It pauses the game, reads the authoritative
/// GameScript observation twice to prove canonical stability, and persists the
/// first exact public observation plus its normalized event stream.
/// </summary>
public sealed class Phase04ObservationBridgeExtension : IPhase03BridgeExtension
{
    public async Task<Phase03BridgeExtensionResult> RunAsync(
        Phase03BridgeExtensionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<Phase03BridgeCheck> checks = [];
        ArenaGameScriptClient gameScript = new(context.Bridge, context.RunId);
        bool paused = false;
        try
        {
            ArenaError? pauseError = await gameScript.PauseAsync(context.RequestTimeout, cancellationToken);
            if (pauseError is not null)
            {
                return Failure(pauseError, "observation-pause", "The simulation could not be paused before observation collection.", checks);
            }

            paused = true;
            checks.Add(Pass("observation-pause", "The simulation paused before authoritative observation collection."));

            GameScriptSnapshotResult firstRead = await gameScript.GetSnapshotAsync(context.RequestTimeout, cancellationToken);
            if (!firstRead.Succeeded || firstRead.Snapshot is null)
            {
                return Failure(firstRead.Error, "observation-contract", "ArenaGS did not return a valid observation.v1 snapshot.", checks);
            }

            if (!firstRead.Snapshot.Paused)
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ProtocolInvalidMessage,
                    "ArenaGS returned an observation while the simulation was not paused.",
                    checks);
            }

            ObservationBuildContext buildContext = ArenaSmokeObservationContext.Create(context.RunId);
            ObservationBuildResult first = ObservationBuilder.Build(firstRead.Snapshot, buildContext);
            checks.Add(Pass("observation-contract", "ArenaGS produced a bounded, typed observation.v1 snapshot."));

            GameScriptSnapshotResult secondRead = await gameScript.GetSnapshotAsync(context.RequestTimeout, cancellationToken);
            if (!secondRead.Succeeded || secondRead.Snapshot is null)
            {
                return Failure(secondRead.Error, "observation-canonical", "ArenaGS did not return a second valid paused observation.", checks);
            }

            ObservationBuildResult second = ObservationBuilder.Build(secondRead.Snapshot, buildContext);
            if (!string.Equals(first.Sha256, second.Sha256, StringComparison.Ordinal))
            {
                return Phase03BridgeExtensionResult.Failure(
                    ArenaErrorCodes.ProtocolInvalidMessage,
                    "Two paused reads of the same authoritative state did not produce byte-stable canonical observations.",
                    checks);
            }

            checks.Add(Pass("observation-canonical", "Two paused reads normalized to the same canonical observation hash."));

            using ObservationArtifactWriter writer = new(context.Paths, context.RunId);
            await writer.AppendObservationAsync(new ObservationBuildRecord(first.Snapshot, first.Sha256, first.ReplaySha256), cancellationToken);
            foreach (NormalizedGameEvent eventEntry in firstRead.Snapshot.Events)
            {
                await writer.AppendEventAsync(eventEntry, cancellationToken);
            }

            checks.Add(Pass(
                "observation-artifacts",
                "The exact provider-facing observation and normalized event stream were persisted under the isolated run root."));
            return Phase03BridgeExtensionResult.Success("The Phase 04 observation proof completed.", checks);
        }
        catch (ArgumentException exception)
        {
            return Phase03BridgeExtensionResult.Failure(
                ArenaErrorCodes.ProtocolInvalidMessage,
                ArtifactTextRedactor.Redact(exception.Message),
                checks);
        }
        finally
        {
            if (paused)
            {
                ArenaError? resumeError = await gameScript.ResumeAsync(context.RequestTimeout, CancellationToken.None);
                if (resumeError is not null)
                {
                    checks.Add(new Phase03BridgeCheck(
                        "observation-resume",
                        false,
                        resumeError.Code,
                        "The simulation could not be resumed after the observation safe boundary."));
                }
                else
                {
                    checks.Add(Pass("observation-resume", "The simulation resumed after observation persistence reached a safe boundary."));
                }
            }
        }
    }

    private static Phase03BridgeCheck Pass(string id, string detail) => new(id, true, null, detail);

    private static Phase03BridgeExtensionResult Failure(
        ArenaError? error,
        string checkId,
        string fallbackDetail,
        IReadOnlyList<Phase03BridgeCheck> checks)
    {
        List<Phase03BridgeCheck> failedChecks = checks.ToList();
        failedChecks.Add(new Phase03BridgeCheck(
            checkId,
            false,
            error?.Code ?? ArenaErrorCodes.ProtocolInvalidMessage,
            fallbackDetail));
        return Phase03BridgeExtensionResult.Failure(
            error?.Code ?? ArenaErrorCodes.ProtocolInvalidMessage,
            fallbackDetail,
            failedChecks);
    }
}
