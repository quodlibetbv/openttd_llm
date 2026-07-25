using System.Text.Json;

namespace OpenTtd.ModelArena.Contracts;

/// <summary>
/// Computes the stable fixture fingerprint for a public observation. The
/// normalised representation is deliberately narrower than the exact
/// observation record: it removes per-run and scheduler-startup jitter while
/// retaining every provider-visible semantic field used by replay fixtures.
/// </summary>
public static class ObservationReplayHasher
{
    public static string ComputeSha256(ObservationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        JsonElement replayObservation = CreateNormalizedObservation(snapshot);
        return CanonicalJson.ComputeSha256(CanonicalJson.Serialize(replayObservation));
    }

    public static JsonElement CreateNormalizedObservation(ObservationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        ObservationSections replaySections = snapshot.Sections with
        {
            GameClock = snapshot.Sections.GameClock with
            {
                GameDate = "0000-00-00",
                GameTick = 0,
            },
            RecentEvents = snapshot.Sections.RecentEvents
                .Select(eventEntry => eventEntry with { GameDate = "0000-00-00" })
                .ToArray(),
            CandidateOpportunities = snapshot.Sections.CandidateOpportunities with
            {
                Towns = snapshot.Sections.CandidateOpportunities.Towns
                    .OrderBy(town => town.TownId)
                    .Select(town => town with { Population = 0 })
                    .ToArray(),
                Opportunities = snapshot.Sections.CandidateOpportunities.Opportunities
                    .OrderBy(opportunity => opportunity.SourceTownId)
                    .ThenBy(opportunity => opportunity.DestinationTownId)
                    .ThenBy(opportunity => opportunity.Cargo, StringComparer.Ordinal)
                    .Select(opportunity => opportunity with { RankingScore = 0 })
                    .ToArray(),
            },
        };
        ObservationSnapshot replaySnapshot = snapshot with
        {
            RunId = "replay",
            GameDate = "0000-00-00",
            Sections = replaySections,
        };
        return JsonSerializer.SerializeToElement(replaySnapshot, ObservationJsonContext.Default.ObservationSnapshot);
    }
}
