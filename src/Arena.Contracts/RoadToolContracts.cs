using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace OpenTtd.ModelArena.Contracts;

public static class RoadToolCatalog
{
    public const string InspectCompany = "inspect_company";
    public const string ListOpportunities = "list_opportunities";
    public const string InspectTown = "inspect_town";
    public const string InspectIndustry = "inspect_industry";
    public const string BuildTransportRoute = "build_transport_route";
    public const string ExpandRoute = "expand_route";
    public const string ReduceRoute = "reduce_route";
    public const string ReplaceVehicles = "replace_vehicles";
    public const string RepayLoan = "repay_loan";
    public const string TakeLoan = "take_loan";
    public const string Wait = "wait";

    public static IReadOnlyList<string> All { get; } =
    [
        InspectCompany,
        ListOpportunities,
        InspectTown,
        InspectIndustry,
        BuildTransportRoute,
        ExpandRoute,
        ReduceRoute,
        ReplaceVehicles,
        RepayLoan,
        TakeLoan,
        Wait,
    ];

    public static bool IsToolIdentifier(string? tool) =>
        tool is { Length: > 0 and <= 80 } &&
        tool[0] is >= 'a' and <= 'z' &&
        tool.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}

/// <summary>
/// Versioned public argument contracts sent with every common model request.
/// These are descriptive metadata only: <see cref="RoadActionValidator"/> and
/// ArenaGS independently enforce the same typed constraints before gameplay.
/// </summary>
public static class RoadToolPromptCatalog
{
    public const string Version = "1.0";

    private static readonly Dictionary<string, ModelToolContract> Contracts =
        new Dictionary<string, ModelToolContract>(StringComparer.Ordinal)
        {
            [RoadToolCatalog.InspectCompany] = NoArguments(
                "Read the authoritative company and financial summary without changing the game."),
            [RoadToolCatalog.ListOpportunities] = NoArguments(
                "Read the bounded authoritative town and industry opportunity set without changing the game."),
            [RoadToolCatalog.InspectTown] = new(
                "Read one authoritative town summary without changing the game.",
                [EntityId("town_id", "observation.sections.candidate_opportunities.towns[].town_id")]),
            [RoadToolCatalog.InspectIndustry] = new(
                "Read one authoritative industry summary without changing the game.",
                [EntityId("industry_id", "observation.sections.candidate_opportunities.industries[].industry_id")]),
            [RoadToolCatalog.BuildTransportRoute] = new(
                "Create one passenger road route. ArenaGS owns all pathfinding, station/depot construction, vehicle selection, orders, and operational verification.",
                [
                    new("mode", "string", "Road transport mode.", AllowedValues: ["road"]),
                    EntityId("source_town_id", "observation.sections.candidate_opportunities.towns[].town_id"),
                    EntityId("destination_town_id", "observation.sections.candidate_opportunities.towns[].town_id"),
                    new("cargo", "string", "Passenger cargo for the Phase 06 road benchmark.", AllowedValues: ["passengers"]),
                    new("initial_vehicle_count", "integer", "Initial compatible passenger vehicles to purchase.", Minimum: 1, Maximum: 8),
                    new("maximum_budget", "integer", "Maximum game-money spend for the whole project.", Minimum: 1, MaximumSource: "observation.sections.constraints_and_budgets.available_project_budget"),
                ]),
            [RoadToolCatalog.ExpandRoute] = FleetChange(
                "Increase an operational route's vehicle count. The target must exceed its current vehicle count."),
            [RoadToolCatalog.ReduceRoute] = new(
                "Reduce an operational route's vehicle count. The target must be below its current vehicle count.",
                [
                    RouteId(),
                    new("vehicle_count", "integer", "Target number of compatible vehicles after reduction.", Minimum: 1, Maximum: 8),
                ]),
            [RoadToolCatalog.ReplaceVehicles] = FleetChange(
                "Replace every vehicle on an operational route with compatible vehicles at the requested target count."),
            [RoadToolCatalog.RepayLoan] = new(
                "Repay a positive loan amount aligned to the current authoritative company loan interval.",
                [new("amount", "integer", "Positive game-money loan adjustment.", Minimum: 1)]),
            [RoadToolCatalog.TakeLoan] = new(
                "Take a positive loan amount aligned to the current authoritative company loan interval and maximum loan.",
                [new("amount", "integer", "Positive game-money loan adjustment.", Minimum: 1)]),
            [RoadToolCatalog.Wait] = new(
                "Advance to the next review interval without construction.",
                [new("game_days", "integer", "Number of game days to wait before review.", Minimum: 1, Maximum: 365)]),
        };

    public static IReadOnlyDictionary<string, ModelToolContract> CreateAllowedContracts(
        IReadOnlyList<string> allowedTools)
    {
        ArgumentNullException.ThrowIfNull(allowedTools);
        Dictionary<string, ModelToolContract> result = new(StringComparer.Ordinal);
        foreach (string tool in allowedTools.Distinct(StringComparer.Ordinal).OrderBy(tool => tool, StringComparer.Ordinal))
        {
            if (!Contracts.TryGetValue(tool, out ModelToolContract? contract))
            {
                throw new ArgumentException("A model request included an unknown road tool contract.", nameof(allowedTools));
            }

            result.Add(tool, contract);
        }

        return result;
    }

    public static IReadOnlyDictionary<string, ModelToolContract> AllContracts => Contracts;

    public static string CanonicalJson { get; } = OpenTtd.ModelArena.Contracts.CanonicalJson.SerializeToString(
        JsonSerializer.SerializeToElement(AllContracts));

    public static string Sha256 { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson))).ToLowerInvariant();

    private static ModelToolContract NoArguments(string description) => new(description, []);

    private static ModelToolContract FleetChange(string description) => new(
        description,
        [
            RouteId(),
            new("vehicle_count", "integer", "Target number of compatible vehicles after the fleet change.", Minimum: 1, Maximum: 8),
            new("maximum_budget", "integer", "Maximum game-money spend for replacement vehicles.", Minimum: 1, MaximumSource: "observation.sections.constraints_and_budgets.available_project_budget"),
        ]);

    private static ModelToolArgumentContract EntityId(string name, string valueSource) =>
        new(name, "integer", "Authoritative entity identifier from the current public observation.", Minimum: 0, ValueSource: valueSource);

    private static ModelToolArgumentContract RouteId() =>
        new("route_id", "string", "Operational route identifier from the current public observation.", ValueSource: "observation.sections.network_summary.routes[].route_id");
}

public sealed record ModelToolContract(
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("arguments")] IReadOnlyList<ModelToolArgumentContract> Arguments);

public sealed record ModelToolArgumentContract(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("required")] bool Required = true,
    [property: JsonPropertyName("minimum"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Minimum = null,
    [property: JsonPropertyName("maximum"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Maximum = null,
    [property: JsonPropertyName("allowed_values"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AllowedValues = null,
    [property: JsonPropertyName("value_source"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ValueSource = null,
    [property: JsonPropertyName("maximum_source"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MaximumSource = null);

/// <summary>
/// The persisted execution stages that a trusted run supervisor may use as a
/// save/load checkpoint boundary. This is deliberately separate from the
/// model-visible tool catalog: a provider can choose only road tools, never a
/// process checkpoint or an AdminPort control command.
/// </summary>
public static class RoadProjectCheckpointStages
{
    public const string Proposed = "proposed";
    public const string Validating = "validating";
    public const string Surveying = "surveying";
    public const string BuildingInfrastructure = "building_infrastructure";
    public const string BuyingVehicles = "buying_vehicles";
    public const string ConfiguringOrders = "configuring_orders";
    public const string Verifying = "verifying";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Proposed,
        Validating,
        Surveying,
        BuildingInfrastructure,
        BuyingVehicles,
        ConfiguringOrders,
        Verifying,
    };
}

public sealed record RoadActionValidationResult(bool IsValid, string? ErrorCode, string Message)
{
    public static RoadActionValidationResult Valid { get; } = new(true, null, "The action is authorized by the latest observation.");

    public static RoadActionValidationResult Invalid(string message) =>
        new(false, ArenaErrorCodes.ActionConstraintViolation, message);
}

/// <summary>
/// Validates only the typed Phase 06 action surface before it crosses
/// AdminPort. ArenaGS repeats equivalent checks and remains authoritative.
/// </summary>
public static class RoadActionValidator
{
    public static RoadActionValidationResult Validate(
        ModelAction action,
        ObservationSnapshot latestSnapshot,
        IReadOnlySet<string> allowedTools)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(latestSnapshot);
        ArgumentNullException.ThrowIfNull(allowedTools);

        if (!RoadToolCatalog.All.Contains(action.Tool, StringComparer.Ordinal) || !allowedTools.Contains(action.Tool))
        {
            return RoadActionValidationResult.Invalid("The selected tool is not allowed for this goal.");
        }

        return action.Tool switch
        {
            RoadToolCatalog.InspectCompany or RoadToolCatalog.ListOpportunities => RequireNoArguments(action.Arguments),
            RoadToolCatalog.InspectTown => RequireEntityId(action.Arguments, "town_id", KnownTownIds(latestSnapshot)),
            RoadToolCatalog.InspectIndustry => RequireEntityId(action.Arguments, "industry_id", KnownIndustryIds(latestSnapshot)),
            RoadToolCatalog.BuildTransportRoute => ValidateBuildTransportRoute(action.Arguments, latestSnapshot),
            RoadToolCatalog.ExpandRoute => ValidateRouteFleetChange(action.Arguments, latestSnapshot, FleetChangeKind.Expand),
            RoadToolCatalog.ReduceRoute => ValidateRouteFleetChange(action.Arguments, latestSnapshot, FleetChangeKind.Reduce),
            RoadToolCatalog.ReplaceVehicles => ValidateRouteFleetChange(action.Arguments, latestSnapshot, FleetChangeKind.Replace),
            RoadToolCatalog.RepayLoan or RoadToolCatalog.TakeLoan => RequirePositiveAmount(action.Arguments),
            RoadToolCatalog.Wait => ValidateWait(action.Arguments),
            _ => RoadActionValidationResult.Invalid("The selected tool is not implemented by the Phase 06 contract."),
        };
    }

    private static RoadActionValidationResult ValidateBuildTransportRoute(JsonElement arguments, ObservationSnapshot snapshot)
    {
        if (!HasOnlyFields(arguments, "mode", "source_town_id", "destination_town_id", "cargo", "initial_vehicle_count", "maximum_budget") ||
            !TryGetString(arguments, "mode", out string? mode) ||
            !string.Equals(mode, "road", StringComparison.Ordinal) ||
            !TryGetInteger(arguments, "source_town_id", out int sourceTownId) ||
            !TryGetInteger(arguments, "destination_town_id", out int destinationTownId) ||
            sourceTownId == destinationTownId ||
            !KnownTownIds(snapshot).Contains(sourceTownId) ||
            !KnownTownIds(snapshot).Contains(destinationTownId) ||
            !TryGetString(arguments, "cargo", out string? cargo) ||
            !string.Equals(cargo, "passengers", StringComparison.Ordinal) ||
            !TryGetInteger(arguments, "initial_vehicle_count", out int vehicleCount) ||
            vehicleCount is < 1 or > 8 ||
            !TryGetLong(arguments, "maximum_budget", out long maximumBudget) ||
            maximumBudget < 1 ||
            maximumBudget > snapshot.Sections.ConstraintsAndBudgets.AvailableProjectBudget)
        {
            return RoadActionValidationResult.Invalid("The road-route request has invalid towns, cargo, fleet size, or budget.");
        }

        return RoadActionValidationResult.Valid;
    }

    private static RoadActionValidationResult ValidateRouteFleetChange(
        JsonElement arguments,
        ObservationSnapshot snapshot,
        FleetChangeKind kind)
    {
        bool requiresBudget = kind is FleetChangeKind.Expand or FleetChangeKind.Replace;
        if (!(requiresBudget
                ? HasOnlyFields(arguments, "route_id", "vehicle_count", "maximum_budget")
                : HasOnlyFields(arguments, "route_id", "vehicle_count")) ||
            !TryGetString(arguments, "route_id", out string? routeId) ||
            !TryGetInteger(arguments, "vehicle_count", out int vehicleCount) ||
            vehicleCount is < 1 or > 8)
        {
            return RoadActionValidationResult.Invalid("The route fleet change must target an operational route, use a bounded target fleet, and declare an affordable budget when it purchases vehicles.");
        }

        ObservationRoute? route = snapshot.Sections.NetworkSummary.Routes.SingleOrDefault(candidate =>
            candidate.Operational &&
            string.Equals(candidate.RouteId, routeId, StringComparison.Ordinal));
        if (route is null ||
            (kind == FleetChangeKind.Expand && vehicleCount <= route.VehicleIds.Count) ||
            (kind == FleetChangeKind.Reduce && vehicleCount >= route.VehicleIds.Count) ||
            (requiresBudget && (!TryGetLong(arguments, "maximum_budget", out long maximumBudget) ||
                                maximumBudget < 1 ||
                                maximumBudget > snapshot.Sections.ConstraintsAndBudgets.AvailableProjectBudget)))
        {
            return RoadActionValidationResult.Invalid("The route fleet change must target an operational route, use a bounded target fleet, and declare an affordable budget when it purchases vehicles.");
        }

        return RoadActionValidationResult.Valid;
    }

    private static RoadActionValidationResult RequireEntityId(JsonElement arguments, string field, HashSet<int> entityIds) =>
        HasOnlyFields(arguments, field) && TryGetInteger(arguments, field, out int entityId) && entityIds.Contains(entityId)
            ? RoadActionValidationResult.Valid
            : RoadActionValidationResult.Invalid("The action does not reference an entity in the latest authoritative observation.");

    private static RoadActionValidationResult RequirePositiveAmount(JsonElement arguments) =>
        HasOnlyFields(arguments, "amount") && TryGetLong(arguments, "amount", out long amount) && amount > 0
            ? RoadActionValidationResult.Valid
            : RoadActionValidationResult.Invalid("The finance action requires one positive integer amount.");

    private static RoadActionValidationResult ValidateWait(JsonElement arguments) =>
        HasOnlyFields(arguments, "game_days") && TryGetInteger(arguments, "game_days", out int gameDays) && gameDays is >= 1 and <= 365
            ? RoadActionValidationResult.Valid
            : RoadActionValidationResult.Invalid("The wait action requires between one and 365 game days.");

    private static RoadActionValidationResult RequireNoArguments(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object && !arguments.EnumerateObject().Any()
            ? RoadActionValidationResult.Valid
            : RoadActionValidationResult.Invalid("This inspection action does not accept arguments.");

    private static HashSet<int> KnownTownIds(ObservationSnapshot snapshot) =>
        snapshot.Sections.CandidateOpportunities.Towns.Select(town => town.TownId).ToHashSet();

    private static HashSet<int> KnownIndustryIds(ObservationSnapshot snapshot) =>
        snapshot.Sections.CandidateOpportunities.Industries.Select(industry => industry.IndustryId).ToHashSet();

    private static bool HasOnlyFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        HashSet<string> expected = new(fields, StringComparer.Ordinal);
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

    private static bool TryGetString(JsonElement value, string field, out string? result)
    {
        result = null;
        if (!value.TryGetProperty(field, out JsonElement property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        result = property.GetString();
        return result is { Length: > 0 and <= 128 };
    }

    private static bool TryGetInteger(JsonElement value, string field, out int result)
    {
        result = 0;
        return value.TryGetProperty(field, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out result) &&
            result >= 0;
    }

    private static bool TryGetLong(JsonElement value, string field, out long result)
    {
        result = 0;
        return value.TryGetProperty(field, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out result);
    }

    private enum FleetChangeKind
    {
        Expand,
        Reduce,
        Replace,
    }
}
