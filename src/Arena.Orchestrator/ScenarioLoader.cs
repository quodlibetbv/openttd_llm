using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Scoring;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace OpenTtd.ModelArena.Orchestrator;

public sealed record ScenarioValidationError(string Field, string Code, string Message);

public sealed record ScenarioDocument(
    string Path,
    string Sha256,
    BenchmarkScenario Scenario);

public sealed record ScenarioLoadResult(
    ScenarioDocument? Document,
    IReadOnlyList<ScenarioValidationError> Errors)
{
    public bool Succeeded => Document is not null && Errors.Count == 0;
}

/// <summary>
/// Strict, bounded YAML reader for public benchmark scenarios. It accepts no
/// local paths, aliases, or unknown fields; every file is confined to the
/// repository and fingerprinted byte-for-byte before it can define a run.
/// </summary>
public static partial class ScenarioLoader
{
    private const int MaximumScenarioBytes = 256 * 1024;
    private const int MaximumYamlDepth = 16;
    private const int MaximumYamlNodes = 1_024;
    private const int MaximumStringLength = 2_000;

    public static Task<ScenarioLoadResult> LoadAsync(
        string repositoryRoot,
        string scenarioPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Load(repositoryRoot, scenarioPath));
    }

    public static ObservationBuildContext CreateObservationContext(
        ScenarioDocument document,
        string runId,
        int remainingModelCalls,
        int remainingOutputTokens,
        int remainingRetries,
        IReadOnlyList<ObservationDecisionResult>? priorDecisionResults = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        BenchmarkScenario scenario = document.Scenario;
        return new ObservationBuildContext(
            RunId: runId,
            ScenarioId: scenario.ScenarioId,
            ScenarioVersion: scenario.Version,
            GoalId: scenario.ScenarioId,
            GoalVersion: scenario.Version,
            GoalTitle: scenario.Title,
            GoalObjective: scenario.Objective,
            AllowedTools: scenario.AllowedTools,
            MinimumCashReserve: scenario.Constraints.MinimumCashReserve,
            PerProjectBudget: scenario.Constraints.PerProjectBudget,
            RemainingModelCalls: remainingModelCalls,
            RemainingOutputTokens: remainingOutputTokens,
            RemainingRetries: remainingRetries,
            PriorDecisionResults: priorDecisionResults ?? [],
            ReductionPolicy: new ObservationReductionPolicy(
                ObservationLimits.Default,
                scenario.Observation.RankingRule,
                scenario.Observation.MaximumCanonicalBytes,
                scenario.Observation.MaximumEstimatedTokens));
    }

    public static ScenarioActionConstraintContext CreateActionConstraintContext(ScenarioDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        BenchmarkScenario scenario = document.Scenario;
        return new ScenarioActionConstraintContext
        {
            ScenarioId = scenario.ScenarioId,
            ScenarioVersion = scenario.Version,
            ScenarioSha256 = document.Sha256,
            MinimumCashReserve = scenario.Constraints.MinimumCashReserve,
            PerProjectBudget = scenario.Constraints.PerProjectBudget,
            MaximumActiveProjects = scenario.Constraints.MaximumActiveProjects,
            AllowedModes = scenario.Constraints.AllowedModes,
            AllowedCargo = scenario.Constraints.AllowedCargo,
            AllowedTools = scenario.AllowedTools,
        };
    }

    private static ScenarioLoadResult Load(string repositoryRoot, string scenarioPath)
    {
        List<ScenarioValidationError> errors = [];
        string? path = ResolveScenarioPath(repositoryRoot, scenarioPath, errors);
        if (path is null)
        {
            return new ScenarioLoadResult(null, errors);
        }

        byte[] bytes;
        try
        {
            FileInfo info = new(path);
            if (!info.Exists || info.Length > MaximumScenarioBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                AddError(errors, "file", "The scenario file is missing, exceeds the bounded size limit, or is a symbolic link.");
                return new ScenarioLoadResult(null, errors);
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            AddError(errors, "file", "The scenario file could not be read safely.");
            return new ScenarioLoadResult(null, errors);
        }
        catch (UnauthorizedAccessException)
        {
            AddError(errors, "file", "The scenario file could not be read safely.");
            return new ScenarioLoadResult(null, errors);
        }

        YamlMappingNode? root = ParseRoot(bytes, errors);
        if (root is null)
        {
            return new ScenarioLoadResult(null, errors);
        }

        Dictionary<string, YamlNode> values = ReadMapping(root, "root", errors);
        ValidateKnownFields(values,
        [
            "schema_version",
            "scenario_id",
            "version",
            "title",
            "world",
            "objective",
            "allowed_tools",
            "constraints",
            "model_budget",
            "objectives",
            "penalties",
            "end_condition",
            "scoring",
            "observation",
            "camera_relevance_hints",
            "replay_tolerances",
        ], "root", errors);

        string? schemaVersion = RequiredString(values, "schema_version", "schema_version", errors, 8);
        string? scenarioId = RequiredIdentifier(values, "scenario_id", "scenario_id", errors);
        string? version = RequiredVersion(values, "version", "version", errors);
        string? title = RequiredString(values, "title", "title", errors, 160);
        string? objective = RequiredString(values, "objective", "objective", errors, MaximumStringLength);
        IReadOnlyList<string>? allowedTools = RequiredStringSequence(values, "allowed_tools", "allowed_tools", errors, 1, RoadToolCatalog.All.Count, 80);
        ScenarioWorld? world = ParseWorld(RequiredMapping(values, "world", "world", errors), errors);
        ScenarioConstraints? constraints = ParseConstraints(RequiredMapping(values, "constraints", "constraints", errors), errors);
        ScenarioModelBudget? modelBudget = ParseModelBudget(RequiredMapping(values, "model_budget", "model_budget", errors), errors);
        IReadOnlyList<ScenarioObjective>? objectives = ParseObjectives(RequiredSequence(values, "objectives", "objectives", errors), errors);
        IReadOnlyList<ScenarioPenalty>? penalties = ParsePenalties(RequiredSequence(values, "penalties", "penalties", errors), errors);
        ScenarioEndCondition? endCondition = ParseEndCondition(RequiredMapping(values, "end_condition", "end_condition", errors), errors);
        ScenarioScoringDefinition? scoring = ParseScoring(RequiredMapping(values, "scoring", "scoring", errors), errors);
        ScenarioObservationPolicy? observation = ParseObservation(RequiredMapping(values, "observation", "observation", errors), errors);
        IReadOnlyList<string>? cameraHints = RequiredStringSequence(values, "camera_relevance_hints", "camera_relevance_hints", errors, 1, 16, 160);
        ReplayMetricTolerances? replayTolerances = ParseReplayTolerances(RequiredMapping(values, "replay_tolerances", "replay_tolerances", errors), errors);

        if (!string.Equals(schemaVersion, ContractVersions.ScenarioV1, StringComparison.Ordinal))
        {
            AddError(errors, "schema_version", "scenario_version must be 1.0.");
        }

        if (allowedTools is not null)
        {
            foreach (string tool in allowedTools)
            {
                if (!RoadToolCatalog.All.Contains(tool, StringComparer.Ordinal))
                {
                    AddError(errors, "allowed_tools", "allowed_tools contains a tool outside the versioned road contract.");
                }
            }
        }

        if (errors.Count > 0 ||
            schemaVersion is null || scenarioId is null || version is null || title is null || objective is null ||
            allowedTools is null || world is null || constraints is null || modelBudget is null || objectives is null ||
            penalties is null || endCondition is null || scoring is null || observation is null || cameraHints is null || replayTolerances is null)
        {
            return new ScenarioLoadResult(null, errors);
        }

        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new ScenarioLoadResult(
            new ScenarioDocument(
                path,
                sha256,
                new BenchmarkScenario
                {
                    SchemaVersion = schemaVersion,
                    ScenarioId = scenarioId,
                    Version = version,
                    Title = title,
                    World = world,
                    Objective = objective,
                    AllowedTools = allowedTools,
                    Constraints = constraints,
                    ModelBudget = modelBudget,
                    Objectives = objectives,
                    Penalties = penalties,
                    EndCondition = endCondition,
                    Scoring = scoring,
                    Observation = observation,
                    CameraRelevanceHints = cameraHints,
                    ReplayTolerances = replayTolerances,
                }),
            errors);
    }

    private static ScenarioWorld? ParseWorld(Dictionary<string, YamlNode> values, List<ScenarioValidationError> errors)
    {
        ValidateKnownFields(values, ["starting_save_id", "content_manifest_id", "game_settings_id", "start_date"], "world", errors);
        string? startingSaveId = RequiredIdentifier(values, "starting_save_id", "world.starting_save_id", errors);
        string? contentManifestId = RequiredIdentifier(values, "content_manifest_id", "world.content_manifest_id", errors);
        string? gameSettingsId = RequiredIdentifier(values, "game_settings_id", "world.game_settings_id", errors);
        string? startDate = RequiredDate(values, "start_date", "world.start_date", errors);
        return startingSaveId is null || contentManifestId is null || gameSettingsId is null || startDate is null
            ? null
            : new ScenarioWorld
            {
                StartingSaveId = startingSaveId,
                ContentManifestId = contentManifestId,
                GameSettingsId = gameSettingsId,
                StartDate = startDate,
            };
    }

    private static ScenarioConstraints? ParseConstraints(Dictionary<string, YamlNode> values, List<ScenarioValidationError> errors)
    {
        ValidateKnownFields(values, ["minimum_cash_reserve", "per_project_budget", "maximum_active_projects", "allowed_modes", "allowed_cargo"], "constraints", errors);
        long? reserve = RequiredLong(values, "minimum_cash_reserve", "constraints.minimum_cash_reserve", 0, 2_000_000_000, errors);
        long? budget = RequiredLong(values, "per_project_budget", "constraints.per_project_budget", 1, 2_000_000_000, errors);
        int? maximumProjects = RequiredInt(values, "maximum_active_projects", "constraints.maximum_active_projects", 1, 16, errors);
        IReadOnlyList<string>? modes = RequiredStringSequence(values, "allowed_modes", "constraints.allowed_modes", errors, 1, 8, 32);
        IReadOnlyList<string>? cargo = RequiredStringSequence(values, "allowed_cargo", "constraints.allowed_cargo", errors, 1, 8, 32);
        if (modes is not null && modes.Any(mode => !string.Equals(mode, "road", StringComparison.Ordinal)))
        {
            AddError(errors, "constraints.allowed_modes", "Phase 07 supports only the road mode.");
        }

        if (cargo is not null && cargo.Any(item => !string.Equals(item, "passengers", StringComparison.Ordinal)))
        {
            AddError(errors, "constraints.allowed_cargo", "Phase 07 supports only passenger cargo.");
        }

        return reserve is null || budget is null || maximumProjects is null || modes is null || cargo is null
            ? null
            : new ScenarioConstraints
            {
                MinimumCashReserve = reserve.Value,
                PerProjectBudget = budget.Value,
                MaximumActiveProjects = maximumProjects.Value,
                AllowedModes = modes,
                AllowedCargo = cargo,
            };
    }

    private static ScenarioModelBudget? ParseModelBudget(Dictionary<string, YamlNode> values, List<ScenarioValidationError> errors)
    {
        ValidateKnownFields(values, ["maximum_calls", "maximum_output_tokens", "maximum_retries"], "model_budget", errors);
        int? calls = RequiredInt(values, "maximum_calls", "model_budget.maximum_calls", 1, 10_000, errors);
        int? tokens = RequiredInt(values, "maximum_output_tokens", "model_budget.maximum_output_tokens", 1, 1_000_000, errors);
        int? retries = RequiredInt(values, "maximum_retries", "model_budget.maximum_retries", 0, 3, errors);
        return calls is null || tokens is null || retries is null
            ? null
            : new ScenarioModelBudget
            {
                MaximumCalls = calls.Value,
                MaximumOutputTokens = tokens.Value,
                MaximumRetries = retries.Value,
            };
    }

    private static List<ScenarioObjective>? ParseObjectives(YamlSequenceNode? sequence, List<ScenarioValidationError> errors)
    {
        if (sequence is null)
        {
            return null;
        }

        if (sequence.Children.Count is < 1 or > 16)
        {
            AddError(errors, "objectives", "objectives must contain between one and sixteen entries.");
            return null;
        }

        List<ScenarioObjective> result = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach ((YamlNode node, int index) in sequence.Children.Select((node, index) => (node, index)))
        {
            string field = $"objectives[{index}]";
            Dictionary<string, YamlNode> values = ReadMapping(node, field, errors);
            ValidateKnownFields(values, ["objective_id", "metric", "minimum"], field, errors);
            string? id = RequiredIdentifier(values, "objective_id", field + ".objective_id", errors);
            string? metric = RequiredString(values, "metric", field + ".metric", errors, 80);
            long? minimum = RequiredLong(values, "minimum", field + ".minimum", 0, 2_000_000_000, errors);
            if (metric is not null && metric is not ("operational_route_count" or "cargo_delivered" or "operating_profit"))
            {
                AddError(errors, field + ".metric", "The objective metric is outside the Phase 07 road benchmark contract.");
            }

            if (id is not null && !ids.Add(id))
            {
                AddError(errors, field + ".objective_id", "Objective identifiers must be unique.");
            }

            if (id is not null && metric is not null && minimum is not null)
            {
                result.Add(new ScenarioObjective { ObjectiveId = id, Metric = metric, Minimum = minimum.Value });
            }
        }

        return result.Count == sequence.Children.Count && errors.All(error => !error.Field.StartsWith("objectives", StringComparison.Ordinal))
            ? result
            : null;
    }

    private static List<ScenarioPenalty>? ParsePenalties(YamlSequenceNode? sequence, List<ScenarioValidationError> errors)
    {
        if (sequence is null)
        {
            return null;
        }

        if (sequence.Children.Count > 16)
        {
            AddError(errors, "penalties", "penalties contains more than the supported sixteen entries.");
            return null;
        }

        List<ScenarioPenalty> result = [];
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach ((YamlNode node, int index) in sequence.Children.Select((node, index) => (node, index)))
        {
            string field = $"penalties[{index}]";
            Dictionary<string, YamlNode> values = ReadMapping(node, field, errors);
            ValidateKnownFields(values, ["key", "trigger", "points"], field, errors);
            string? key = RequiredIdentifier(values, "key", field + ".key", errors);
            string? trigger = RequiredString(values, "trigger", field + ".trigger", errors, 80);
            decimal? points = RequiredDecimal(values, "points", field + ".points", 0m, 1_000_000m, errors);
            if (trigger is not null && trigger is not ("invalid_decision" or "constraint_violation"))
            {
                AddError(errors, field + ".trigger", "The penalty trigger is outside the Phase 07 contract.");
            }

            if (key is not null && !keys.Add(key))
            {
                AddError(errors, field + ".key", "Penalty keys must be unique.");
            }

            if (key is not null && trigger is not null && points is not null)
            {
                result.Add(new ScenarioPenalty { Key = key, Trigger = trigger, Points = points.Value });
            }
        }

        return result.Count == sequence.Children.Count && errors.All(error => !error.Field.StartsWith("penalties", StringComparison.Ordinal))
            ? result
            : null;
    }

    private static ScenarioEndCondition? ParseEndCondition(Dictionary<string, YamlNode> values, List<ScenarioValidationError> errors)
    {
        ValidateKnownFields(values, ["type", "value"], "end_condition", errors);
        string? type = RequiredString(values, "type", "end_condition.type", errors, 80);
        string? value = RequiredString(values, "value", "end_condition.value", errors, 100);
        if (type is not null && type is not ("game_date" or "bankruptcy" or "goal_completed" or "model_budget_exhausted"))
        {
            AddError(errors, "end_condition.type", "The end-condition type is outside the Phase 07 contract.");
        }

        return type is null || value is null ? null : new ScenarioEndCondition { Type = type, Value = value };
    }

    private static ScenarioScoringDefinition? ParseScoring(Dictionary<string, YamlNode> values, List<ScenarioValidationError> errors)
    {
        ValidateKnownFields(values, ["score_schema_version", "formula_id", "components"], "scoring", errors);
        string? version = RequiredString(values, "score_schema_version", "scoring.score_schema_version", errors, 8);
        string? formula = RequiredString(values, "formula_id", "scoring.formula_id", errors, 80);
        IReadOnlyList<ScenarioScoreComponentDefinition>? components = ParseScoreComponents(RequiredSequence(values, "components", "scoring.components", errors), errors);
        if (!string.Equals(version, ContractVersions.ScoreV1, StringComparison.Ordinal))
        {
            AddError(errors, "scoring.score_schema_version", "score_schema_version must be 1.0.");
        }

        if (!string.Equals(formula, RoadProfitScoreCalculator.FormulaId, StringComparison.Ordinal))
        {
            AddError(errors, "scoring.formula_id", "formula_id must be road-profit-v1.");
        }

        return version is null || formula is null || components is null
            ? null
            : new ScenarioScoringDefinition { ScoreSchemaVersion = version, FormulaId = formula, Components = components };
    }

    private static List<ScenarioScoreComponentDefinition>? ParseScoreComponents(YamlSequenceNode? sequence, List<ScenarioValidationError> errors)
    {
        if (sequence is null)
        {
            return null;
        }

        if (sequence.Children.Count != 6)
        {
            AddError(errors, "scoring.components", "The Phase 07 road-profit formula requires its six declared components.");
            return null;
        }

        string[] requiredKeys = ["operating_profit", "company_value", "cargo_delivered", "profit_per_active_vehicle", "return_on_infrastructure", "solvency_completion"];
        List<ScenarioScoreComponentDefinition> result = [];
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach ((YamlNode node, int index) in sequence.Children.Select((node, index) => (node, index)))
        {
            string field = $"scoring.components[{index}]";
            Dictionary<string, YamlNode> values = ReadMapping(node, field, errors);
            ValidateKnownFields(values, ["key", "metric", "units", "baseline", "cap", "weight", "missing_data_behavior", "penalty_interaction"], field, errors);
            string? key = RequiredString(values, "key", field + ".key", errors, 80);
            string? metric = RequiredString(values, "metric", field + ".metric", errors, 80);
            string? units = RequiredString(values, "units", field + ".units", errors, 80);
            decimal? baseline = RequiredDecimal(values, "baseline", field + ".baseline", -1_000_000_000m, 1_000_000_000m, errors);
            decimal? cap = RequiredDecimal(values, "cap", field + ".cap", -1_000_000_000m, 1_000_000_000m, errors);
            decimal? weight = RequiredDecimal(values, "weight", field + ".weight", 0m, 1_000_000m, errors);
            string? missing = RequiredString(values, "missing_data_behavior", field + ".missing_data_behavior", errors, 32);
            string? penalty = RequiredString(values, "penalty_interaction", field + ".penalty_interaction", errors, 64);
            if (key is not null && !keys.Add(key))
            {
                AddError(errors, field + ".key", "Score component keys must be unique.");
            }

            if (key is not null && !requiredKeys.Contains(key, StringComparer.Ordinal))
            {
                AddError(errors, field + ".key", "The score component is outside the road-profit formula.");
            }

            if (key is not null && metric is not null && !string.Equals(key, metric, StringComparison.Ordinal))
            {
                AddError(errors, field + ".metric", "Each Phase 07 score component must use its matching canonical metric.");
            }

            if (baseline is not null && cap is not null && cap <= baseline)
            {
                AddError(errors, field + ".cap", "Every score component cap must exceed its baseline.");
            }

            if (missing is not null && missing is not "zero" and not "fail")
            {
                AddError(errors, field + ".missing_data_behavior", "missing_data_behavior must be zero or fail.");
            }

            if (!string.Equals(penalty, "subtract_after_component_normalization", StringComparison.Ordinal))
            {
                AddError(errors, field + ".penalty_interaction", "penalty_interaction must declare the Phase 07 subtraction order.");
            }

            if (key is not null && metric is not null && units is not null && baseline is not null && cap is not null && weight is not null && missing is not null && penalty is not null)
            {
                result.Add(new ScenarioScoreComponentDefinition
                {
                    Key = key,
                    Metric = metric,
                    Units = units,
                    Baseline = baseline.Value,
                    Cap = cap.Value,
                    Weight = weight.Value,
                    MissingDataBehavior = missing,
                    PenaltyInteraction = penalty,
                });
            }
        }

        if (!keys.SetEquals(requiredKeys))
        {
            AddError(errors, "scoring.components", "The score component set must contain each required road-profit component exactly once.");
        }

        return result.Count == sequence.Children.Count && errors.All(error => !error.Field.StartsWith("scoring.components", StringComparison.Ordinal))
            ? result
            : null;
    }

    private static ScenarioObservationPolicy? ParseObservation(Dictionary<string, YamlNode> values, List<ScenarioValidationError> errors)
    {
        ValidateKnownFields(values, ["ranking_rule", "maximum_canonical_bytes", "maximum_estimated_tokens"], "observation", errors);
        string? rankingRule = RequiredString(values, "ranking_rule", "observation.ranking_rule", errors, 80);
        int? bytes = RequiredInt(values, "maximum_canonical_bytes", "observation.maximum_canonical_bytes", 1_024, 12 * 1024, errors);
        int? tokens = RequiredInt(values, "maximum_estimated_tokens", "observation.maximum_estimated_tokens", 256, 8_192, errors);
        if (!string.Equals(rankingRule, "population_then_distance", StringComparison.Ordinal))
        {
            AddError(errors, "observation.ranking_rule", "Phase 07 supports only the versioned population_then_distance reduction rule.");
        }

        return rankingRule is null || bytes is null || tokens is null
            ? null
            : new ScenarioObservationPolicy
            {
                RankingRule = rankingRule,
                MaximumCanonicalBytes = bytes.Value,
                MaximumEstimatedTokens = tokens.Value,
            };
    }

    private static ReplayMetricTolerances? ParseReplayTolerances(Dictionary<string, YamlNode> values, List<ScenarioValidationError> errors)
    {
        string[] fields = ["cash", "operating_profit", "company_value", "cargo_delivered", "active_vehicle_count", "operational_route_count", "infrastructure_investment"];
        ValidateKnownFields(values, fields, "replay_tolerances", errors);
        long? cash = RequiredLong(values, "cash", "replay_tolerances.cash", 0, 2_000_000_000, errors);
        long? profit = RequiredLong(values, "operating_profit", "replay_tolerances.operating_profit", 0, 2_000_000_000, errors);
        long? value = RequiredLong(values, "company_value", "replay_tolerances.company_value", 0, 2_000_000_000, errors);
        long? cargo = RequiredLong(values, "cargo_delivered", "replay_tolerances.cargo_delivered", 0, 2_000_000_000, errors);
        long? vehicles = RequiredLong(values, "active_vehicle_count", "replay_tolerances.active_vehicle_count", 0, 16, errors);
        long? routes = RequiredLong(values, "operational_route_count", "replay_tolerances.operational_route_count", 0, 16, errors);
        long? infrastructure = RequiredLong(values, "infrastructure_investment", "replay_tolerances.infrastructure_investment", 0, 2_000_000_000, errors);
        return cash is null || profit is null || value is null || cargo is null || vehicles is null || routes is null || infrastructure is null
            ? null
            : new ReplayMetricTolerances
            {
                Cash = cash.Value,
                OperatingProfit = profit.Value,
                CompanyValue = value.Value,
                CargoDelivered = cargo.Value,
                ActiveVehicleCount = vehicles.Value,
                OperationalRouteCount = routes.Value,
                InfrastructureInvestment = infrastructure.Value,
            };
    }

    private static YamlMappingNode? ParseRoot(byte[] bytes, List<ScenarioValidationError> errors)
    {
        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            YamlStream yaml = new();
            yaml.Load(reader);
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            {
                AddError(errors, "root", "A scenario must contain exactly one YAML mapping document.");
                return null;
            }

            int nodes = 0;
            if (!IsBoundedYamlNode(root, 0, ref nodes))
            {
                AddError(errors, "root", "The scenario exceeds the YAML depth, node-count, or alias safety limits.");
                return null;
            }

            return root;
        }
        catch (YamlException)
        {
            AddError(errors, "file", "The scenario file is not valid YAML.");
            return null;
        }
    }

    private static bool IsBoundedYamlNode(YamlNode node, int depth, ref int nodes)
    {
        nodes += 1;
        if (depth > MaximumYamlDepth || nodes > MaximumYamlNodes || node.NodeType == YamlNodeType.Alias)
        {
            return false;
        }

        if (node is YamlMappingNode mapping)
        {
            foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
            {
                if (!IsBoundedYamlNode(pair.Key, depth + 1, ref nodes) ||
                    !IsBoundedYamlNode(pair.Value, depth + 1, ref nodes))
                {
                    return false;
                }
            }

            return true;
        }

        if (node is YamlSequenceNode sequence)
        {
            foreach (YamlNode child in sequence.Children)
            {
                if (!IsBoundedYamlNode(child, depth + 1, ref nodes))
                {
                    return false;
                }
            }

            return true;
        }

        return node is YamlScalarNode scalar && scalar.Value is { Length: <= MaximumStringLength };
    }

    private static Dictionary<string, YamlNode> ReadMapping(YamlNode? node, string field, List<ScenarioValidationError> errors)
    {
        Dictionary<string, YamlNode> values = new(StringComparer.Ordinal);
        if (node is not YamlMappingNode mapping)
        {
            AddError(errors, field, "The value must be a mapping.");
            return values;
        }

        foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { Length: > 0 and <= 128 } key } || !values.TryAdd(key, valueNode))
            {
                AddError(errors, field, "The mapping has an invalid or duplicate field name.");
            }
        }

        return values;
    }

    private static Dictionary<string, YamlNode> RequiredMapping(
        Dictionary<string, YamlNode> values,
        string key,
        string field,
        List<ScenarioValidationError> errors)
    {
        if (!values.TryGetValue(key, out YamlNode? node))
        {
            AddError(errors, field, "The required mapping is absent.");
            return [];
        }

        return ReadMapping(node, field, errors);
    }

    private static YamlSequenceNode? RequiredSequence(
        Dictionary<string, YamlNode> values,
        string key,
        string field,
        List<ScenarioValidationError> errors)
    {
        if (!values.TryGetValue(key, out YamlNode? node) || node is not YamlSequenceNode sequence)
        {
            AddError(errors, field, "The required value must be a sequence.");
            return null;
        }

        return sequence;
    }

    private static string? RequiredString(
        Dictionary<string, YamlNode> values,
        string key,
        string field,
        List<ScenarioValidationError> errors,
        int maximumLength)
    {
        if (!values.TryGetValue(key, out YamlNode? node) || node is not YamlScalarNode { Value: { } value } ||
            string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            AddError(errors, field, "The required value must be a bounded non-empty string.");
            return null;
        }

        return value;
    }

    private static string? RequiredIdentifier(
        Dictionary<string, YamlNode> values,
        string key,
        string field,
        List<ScenarioValidationError> errors)
    {
        string? value = RequiredString(values, key, field, errors, 128);
        if (value is not null && !ProtocolEnvelopeValidator.IsIdentifier(value))
        {
            AddError(errors, field, "The value must be a versioned Arena identifier.");
            return null;
        }

        return value;
    }

    private static string? RequiredVersion(
        Dictionary<string, YamlNode> values,
        string key,
        string field,
        List<ScenarioValidationError> errors)
    {
        string? value = RequiredString(values, key, field, errors, 32);
        if (value is not null && !SemanticVersionPattern().IsMatch(value))
        {
            AddError(errors, field, "The value must be a semantic version with major, minor, and patch parts.");
            return null;
        }

        return value;
    }

    private static string? RequiredDate(
        Dictionary<string, YamlNode> values,
        string key,
        string field,
        List<ScenarioValidationError> errors)
    {
        string? value = RequiredString(values, key, field, errors, 10);
        if (value is not null && !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            AddError(errors, field, "The value must use the YYYY-MM-DD game-date form.");
            return null;
        }

        return value;
    }

    private static int? RequiredInt(
        Dictionary<string, YamlNode> values,
        string key,
        string field,
        int minimum,
        int maximum,
        List<ScenarioValidationError> errors)
    {
        long? value = RequiredLong(values, key, field, minimum, maximum, errors);
        return value is null ? null : (int)value.Value;
    }

    private static long? RequiredLong(
        Dictionary<string, YamlNode> values,
        string key,
        string field,
        long minimum,
        long maximum,
        List<ScenarioValidationError> errors)
    {
        if (!values.TryGetValue(key, out YamlNode? node) || node is not YamlScalarNode { Value: { } text } ||
            !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ||
            value < minimum || value > maximum)
        {
            AddError(errors, field, $"The required integer must be between {minimum} and {maximum}.");
            return null;
        }

        return value;
    }

    private static decimal? RequiredDecimal(
        Dictionary<string, YamlNode> values,
        string key,
        string field,
        decimal minimum,
        decimal maximum,
        List<ScenarioValidationError> errors)
    {
        if (!values.TryGetValue(key, out YamlNode? node) || node is not YamlScalarNode { Value: { } text } ||
            !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value) ||
            value < minimum || value > maximum)
        {
            AddError(errors, field, "The required decimal is outside its supported range.");
            return null;
        }

        return value;
    }

    private static List<string>? RequiredStringSequence(
        Dictionary<string, YamlNode> values,
        string key,
        string field,
        List<ScenarioValidationError> errors,
        int minimumItems,
        int maximumItems,
        int maximumStringLength)
    {
        YamlSequenceNode? sequence = RequiredSequence(values, key, field, errors);
        if (sequence is null)
        {
            return null;
        }

        if (sequence.Children.Count < minimumItems || sequence.Children.Count > maximumItems)
        {
            AddError(errors, field, $"The sequence must contain between {minimumItems} and {maximumItems} entries.");
            return null;
        }

        List<string> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (YamlNode child in sequence.Children)
        {
            if (child is not YamlScalarNode { Value: { } value } || string.IsNullOrWhiteSpace(value) || value.Length > maximumStringLength || !seen.Add(value))
            {
                AddError(errors, field, "The sequence contains an invalid or duplicate string.");
                return null;
            }

            result.Add(value);
        }

        return result;
    }

    private static void ValidateKnownFields(
        IReadOnlyDictionary<string, YamlNode> values,
        IReadOnlyCollection<string> allowed,
        string field,
        List<ScenarioValidationError> errors)
    {
        foreach (string key in values.Keys)
        {
            if (!allowed.Contains(key, StringComparer.Ordinal))
            {
                AddError(errors, field + "." + key, "Unknown scenario fields are rejected.");
            }
        }
    }

    private static string? ResolveScenarioPath(string repositoryRoot, string scenarioPath, List<ScenarioValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot) || string.IsNullOrWhiteSpace(scenarioPath))
        {
            AddError(errors, "file", "The repository root or scenario path is invalid.");
            return null;
        }

        string root = Path.GetFullPath(repositoryRoot);
        string candidate = Path.IsPathRooted(scenarioPath)
            ? Path.GetFullPath(scenarioPath)
            : Path.GetFullPath(Path.Combine(root, scenarioPath));
        if (!IsWithinRoot(root, candidate) ||
            (!candidate.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) && !candidate.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
        {
            AddError(errors, "file", "Scenarios must be YAML files below the repository root.");
            return null;
        }

        return candidate;
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        string normalizedRoot = Path.GetFullPath(root);
        string normalizedCandidate = Path.GetFullPath(candidate);
        string rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddError(List<ScenarioValidationError> errors, string field, string message) =>
        errors.Add(new ScenarioValidationError(field, ArenaErrorCodes.ScenarioInvalid, message));

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionPattern();
}
