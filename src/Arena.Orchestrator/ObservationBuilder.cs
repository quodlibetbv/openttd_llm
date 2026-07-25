using System.Text.Json;
using OpenTtd.ModelArena.Contracts;

namespace OpenTtd.ModelArena.Orchestrator;

public sealed record ObservationLimits(
    int MaximumTowns,
    int MaximumIndustries,
    int MaximumStations,
    int MaximumVehicles,
    int MaximumRoutes,
    int MaximumProjects,
    int MaximumOpportunities,
    int MaximumEvents,
    int MaximumDecisionResults)
{
    public static ObservationLimits Default { get; } = new(
        MaximumTowns: 8,
        MaximumIndustries: 8,
        MaximumStations: 8,
        MaximumVehicles: 8,
        MaximumRoutes: 8,
        MaximumProjects: 8,
        MaximumOpportunities: 8,
        MaximumEvents: 16,
        MaximumDecisionResults: 8);

    public void Validate()
    {
        int[] limits =
        [
            MaximumTowns,
            MaximumIndustries,
            MaximumStations,
            MaximumVehicles,
            MaximumRoutes,
            MaximumProjects,
            MaximumOpportunities,
            MaximumEvents,
            MaximumDecisionResults,
        ];
        if (limits.Any(limit => limit is < 1 or > 16))
        {
            throw new ArgumentOutOfRangeException(nameof(ObservationLimits), "Observation limits must be between one and sixteen entities per section.");
        }
    }
}

/// <summary>
/// Scenario-declared reduction policy. Phase 04 supports one transparent
/// ranking rule; future scenarios must select a versioned rule rather than
/// adding goal-specific heuristics to an individual provider request.
/// </summary>
public sealed record ObservationReductionPolicy(
    ObservationLimits Limits,
    string RankingRule,
    int MaximumCanonicalBytes = 12 * 1024,
    int MaximumEstimatedTokens = 4_096)
{
    public static ObservationReductionPolicy Default { get; } = new(
        ObservationLimits.Default,
        "population_then_distance");

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Limits);
        Limits.Validate();
        if (!string.Equals(RankingRule, "population_then_distance", StringComparison.Ordinal) ||
            MaximumCanonicalBytes is < 1_024 or > 12 * 1024 ||
            MaximumEstimatedTokens is < 256 or > 8_192)
        {
            throw new ArgumentOutOfRangeException(nameof(ObservationReductionPolicy), "The observation reduction policy is outside the supported v1 platform bounds.");
        }
    }
}

public sealed record ObservationBuildContext(
    string RunId,
    string ScenarioId,
    string ScenarioVersion,
    string GoalId,
    string GoalVersion,
    string GoalTitle,
    string GoalObjective,
    IReadOnlyList<string> AllowedTools,
    long MinimumCashReserve,
    long PerProjectBudget,
    int RemainingModelCalls,
    int RemainingOutputTokens,
    int RemainingRetries,
    IReadOnlyList<ObservationDecisionResult> PriorDecisionResults,
    ObservationReductionPolicy? ReductionPolicy = null);

public sealed record ObservationBuildResult(
    ObservationSnapshot Snapshot,
    JsonElement CanonicalJson,
    string Sha256,
    string ReplaySha256);

/// <summary>
/// Converts a bounded authoritative ArenaGS snapshot into the exact public
/// observation contract. All ordering happens here, so independent providers
/// receive the same bytes and replay hashes for the same game state.
/// </summary>
public static class ObservationBuilder
{
    public static ObservationBuildResult Build(
        GameScriptSnapshot gameState,
        ObservationBuildContext context,
        ObservationLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(gameState);
        ArgumentNullException.ThrowIfNull(context);
        ObservationReductionPolicy policy = context.ReductionPolicy ?? ObservationReductionPolicy.Default;
        policy.Validate();
        ObservationLimits effectiveLimits = limits ?? policy.Limits;
        effectiveLimits.Validate();
        ValidateContext(context);

        IReadOnlyList<GameTownState> townsByPopulation = gameState.Towns
            .OrderByDescending(town => town.Population)
            .ThenBy(town => town.TownId)
            .Take(effectiveLimits.MaximumTowns)
            .Select(SanitizeTown)
            .ToArray();
        IReadOnlyList<GameTownState> towns = townsByPopulation
            .OrderBy(town => town.TownId)
            .ToArray();
        IReadOnlyList<GameIndustryState> industries = gameState.Industries
            .OrderBy(industry => industry.IndustryId)
            .Take(effectiveLimits.MaximumIndustries)
            .Select(SanitizeIndustry)
            .ToArray();
        IReadOnlyList<GameStationState> stations = gameState.Stations
            .OrderBy(station => station.StationId)
            .Take(effectiveLimits.MaximumStations)
            .Select(SanitizeStation)
            .ToArray();
        IReadOnlyList<GameVehicleState> vehicles = gameState.Vehicles
            .OrderBy(vehicle => vehicle.VehicleId)
            .Take(effectiveLimits.MaximumVehicles)
            .Select(SanitizeVehicle)
            .ToArray();
        IReadOnlyList<ObservationRoute> routes = gameState.Routes
            .OrderBy(route => route.RouteId, StringComparer.Ordinal)
            .Take(effectiveLimits.MaximumRoutes)
            .Select(SanitizeRoute)
            .ToArray();
        IReadOnlyList<ObservationProject> projects = gameState.Projects
            .OrderBy(project => project.ProjectId, StringComparer.Ordinal)
            .Take(effectiveLimits.MaximumProjects)
            .Select(project => new ObservationProject
            {
                ProjectId = project.ProjectId,
                ActionId = project.ActionId,
                State = project.State,
                Spent = Math.Max(0, project.Spent),
                MaximumBudget = Math.Max(1, project.MaximumBudget),
                FailureCode = SanitizeFailureCode(project.FailureCode),
            })
            .ToArray();
        IReadOnlyList<NormalizedGameEvent> events = gameState.Events
            .OrderBy(eventEntry => eventEntry.GameDate, StringComparer.Ordinal)
            .ThenBy(eventEntry => eventEntry.EventId, StringComparer.Ordinal)
            .Take(effectiveLimits.MaximumEvents)
            .Select(SanitizeEvent)
            .ToArray();
        IReadOnlyList<ObservationDecisionResult> priorResults = context.PriorDecisionResults
            .OrderBy(result => result.DecisionId, StringComparer.Ordinal)
            .ThenBy(result => result.ActionId, StringComparer.Ordinal)
            .Take(effectiveLimits.MaximumDecisionResults)
            .Select(SanitizeDecisionResult)
            .ToArray();

        IReadOnlyList<ObservationOpportunity> opportunities = BuildTownOpportunities(towns, policy.RankingRule)
            .Take(effectiveLimits.MaximumOpportunities)
            .ToArray();
        long availableProjectBudget = Math.Max(0, Math.Min(
            context.PerProjectBudget,
            gameState.Company.Cash - context.MinimumCashReserve));

        ObservationSnapshot snapshot = new()
        {
            SchemaVersion = ContractVersions.ObservationV1,
            RunId = context.RunId,
            GameDate = gameState.GameDate,
            Sections = new ObservationSections
            {
                RunContext = new ObservationRunContext
                {
                    ScenarioId = context.ScenarioId,
                    ScenarioVersion = context.ScenarioVersion,
                    BenchmarkCompanyId = gameState.Company.CompanyId,
                    Currency = "game_money",
                },
                GoalContext = new ObservationGoalContext
                {
                    GoalId = context.GoalId,
                    GoalVersion = context.GoalVersion,
                    Title = PublicTextSanitizer.Sanitize(context.GoalTitle, 160, "Arena goal"),
                    Objective = PublicTextSanitizer.Sanitize(context.GoalObjective, 2000, "Complete the declared Arena goal."),
                    AllowedTools = context.AllowedTools.OrderBy(tool => tool, StringComparer.Ordinal).ToArray(),
                    RankingRule = policy.RankingRule,
                },
                GameClock = new ObservationGameClock
                {
                    GameDate = gameState.GameDate,
                    GameTick = Math.Max(0, gameState.GameTick),
                    Paused = gameState.Paused,
                },
                CompanySummary = new ObservationCompanySummary
                {
                    CompanyId = gameState.Company.CompanyId,
                    Name = PublicTextSanitizer.Sanitize(gameState.Company.Name, 160, "Arena company"),
                    VehicleCount = gameState.Vehicles.Count,
                    StationCount = gameState.Stations.Count,
                    RouteCount = gameState.Routes.Count,
                },
                FinancialSummary = new ObservationFinancialSummary
                {
                    Currency = "game_money",
                    Cash = gameState.Company.Cash,
                    Loan = Math.Max(0, gameState.Company.Loan),
                    QuarterlyIncome = gameState.Company.QuarterlyIncome,
                    QuarterlyExpenses = gameState.Company.QuarterlyExpenses,
                    QuarterlyProfit = gameState.Company.QuarterlyIncome - gameState.Company.QuarterlyExpenses,
                    CompanyValue = gameState.Company.CompanyValue,
                    PerformanceRating = Math.Max(0, gameState.Company.PerformanceRating),
                },
                NetworkSummary = new ObservationNetworkSummary
                {
                    Stations = stations,
                    Vehicles = vehicles,
                    Routes = routes,
                },
                ActiveProjects = projects,
                ConstraintsAndBudgets = new ObservationConstraintsAndBudgets
                {
                    MinimumCashReserve = context.MinimumCashReserve,
                    PerProjectBudget = context.PerProjectBudget,
                    AvailableProjectBudget = availableProjectBudget,
                },
                CandidateOpportunities = new ObservationCandidateOpportunities
                {
                    Towns = towns,
                    Industries = industries,
                    Opportunities = opportunities,
                },
                RecentEvents = events,
                PriorDecisionResults = priorResults,
                RemainingModelBudget = new ObservationModelBudget
                {
                    RemainingCalls = context.RemainingModelCalls,
                    RemainingOutputTokens = context.RemainingOutputTokens,
                    RemainingRetries = context.RemainingRetries,
                },
            },
        };

        JsonElement serialised = JsonSerializer.SerializeToElement(snapshot, ObservationJsonContext.Default.ObservationSnapshot);
        byte[] canonicalBytes = CanonicalJson.Serialize(serialised);
        int estimatedTokens = (canonicalBytes.Length + 2) / 3;
        if (canonicalBytes.Length > policy.MaximumCanonicalBytes || estimatedTokens > policy.MaximumEstimatedTokens)
        {
            throw new InvalidOperationException("The normalized observation exceeds its scenario-declared byte or estimated token budget.");
        }

        using JsonDocument document = JsonDocument.Parse(canonicalBytes);
        return new ObservationBuildResult(
            snapshot,
            document.RootElement.Clone(),
            CanonicalJson.ComputeSha256(canonicalBytes),
            ObservationReplayHasher.ComputeSha256(snapshot));
    }

    private static IEnumerable<ObservationOpportunity> BuildTownOpportunities(
        IReadOnlyList<GameTownState> towns,
        string rankingRule)
    {
        if (!string.Equals(rankingRule, "population_then_distance", StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(rankingRule), "The observation builder does not implement the selected ranking rule.");
        }

        List<ObservationOpportunity> opportunities = [];
        for (int sourceIndex = 0; sourceIndex < towns.Count; sourceIndex++)
        {
            for (int destinationIndex = sourceIndex + 1; destinationIndex < towns.Count; destinationIndex++)
            {
                GameTownState source = towns[sourceIndex];
                GameTownState destination = towns[destinationIndex];
                int distance = Math.Abs(source.Location.X - destination.Location.X) + Math.Abs(source.Location.Y - destination.Location.Y);
                if (distance < 2)
                {
                    continue;
                }

                long score = Math.Max(0, (long)(source.Population + destination.Population) - distance);
                opportunities.Add(new ObservationOpportunity
                {
                    OpportunityId = $"opportunity-{source.TownId}-{destination.TownId}-passengers",
                    Kind = "town_pair",
                    SourceTownId = source.TownId,
                    SourceTownName = source.Name,
                    DestinationTownId = destination.TownId,
                    DestinationTownName = destination.Name,
                    Cargo = "passengers",
                    DistanceTiles = distance,
                    RankingScore = score > int.MaxValue ? int.MaxValue : (int)score,
                });
            }
        }

        return opportunities
            .OrderByDescending(opportunity => opportunity.RankingScore)
            .ThenBy(opportunity => opportunity.DistanceTiles)
            .ThenBy(opportunity => opportunity.SourceTownId)
            .ThenBy(opportunity => opportunity.DestinationTownId);
    }

    private static void ValidateContext(ObservationBuildContext context)
    {
        if (!ProtocolEnvelopeValidator.IsIdentifier(context.RunId) ||
            !ProtocolEnvelopeValidator.IsIdentifier(context.ScenarioId) ||
            !ProtocolEnvelopeValidator.IsIdentifier(context.GoalId) ||
            context.ScenarioVersion.Length is < 5 or > 32 ||
            context.GoalVersion.Length is < 5 or > 32 ||
            context.MinimumCashReserve < 0 ||
            context.PerProjectBudget < 1 ||
            context.RemainingModelCalls < 0 ||
            context.RemainingOutputTokens < 0 ||
            context.RemainingRetries is < 0 or > 1 ||
            context.AllowedTools.Count is < 1 or > 32 ||
            context.AllowedTools.Any(tool => !RoadToolCatalog.All.Contains(tool, StringComparer.Ordinal)))
        {
            throw new ArgumentException("Observation context does not meet the v1 contract bounds.", nameof(context));
        }
    }

    private static GameTownState SanitizeTown(GameTownState town) =>
        town with
        {
            Name = PublicTextSanitizer.Sanitize(town.Name, 160, "Unknown town"),
            Population = Math.Max(0, town.Population),
            Location = SanitizeCoordinate(town.Location),
        };

    private static GameIndustryState SanitizeIndustry(GameIndustryState industry) =>
        industry with
        {
            Name = PublicTextSanitizer.Sanitize(industry.Name, 160, "Unknown industry"),
            Location = SanitizeCoordinate(industry.Location),
        };

    private static GameStationState SanitizeStation(GameStationState station) =>
        station with
        {
            Name = PublicTextSanitizer.Sanitize(station.Name, 160, "Unknown station"),
            Location = SanitizeCoordinate(station.Location),
            VehicleCount = Math.Max(0, station.VehicleCount),
        };

    private static GameVehicleState SanitizeVehicle(GameVehicleState vehicle) =>
        vehicle with
        {
            Name = PublicTextSanitizer.Sanitize(vehicle.Name, 160, "Unknown vehicle"),
            Location = SanitizeCoordinate(vehicle.Location),
        };

    private static ObservationRoute SanitizeRoute(GameRouteState route) =>
        new()
        {
            RouteId = route.RouteId,
            ActionId = route.ActionId,
            SourceStationId = Math.Max(0, route.SourceStationId),
            DestinationStationId = Math.Max(0, route.DestinationStationId),
            Cargo = PublicTextSanitizer.Sanitize(route.Cargo, 64, "unknown"),
            VehicleIds = route.VehicleIds.OrderBy(vehicleId => vehicleId).Take(16).ToArray(),
            Operational = route.Operational,
        };

    private static NormalizedGameEvent SanitizeEvent(NormalizedGameEvent eventEntry) =>
        eventEntry with
        {
            EntityIds = eventEntry.EntityIds.OrderBy(entityId => entityId, StringComparer.Ordinal).Take(16).ToArray(),
            PublicSummary = PublicTextSanitizer.Sanitize(eventEntry.PublicSummary, 500, "Arena event recorded."),
        };

    private static ObservationDecisionResult SanitizeDecisionResult(ObservationDecisionResult result) =>
        result with { Message = PublicTextSanitizer.Sanitize(result.Message, 500, "Action result recorded.") };

    private static string? SanitizeFailureCode(string? failureCode) =>
        failureCode is { Length: > 0 and <= 128 } &&
        failureCode.StartsWith("ARENA-", StringComparison.Ordinal) &&
        failureCode.All(character =>
            (character >= 'A' && character <= 'Z') ||
            (character >= '0' && character <= '9') ||
            character == '-')
            ? failureCode
            : null;

    private static TileCoordinate SanitizeCoordinate(TileCoordinate coordinate) =>
        coordinate with { X = Math.Max(0, coordinate.X), Y = Math.Max(0, coordinate.Y) };
}
