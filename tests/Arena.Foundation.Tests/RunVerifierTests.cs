using System.Security.Cryptography;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Orchestrator;
using OpenTtd.ModelArena.Scoring;
using OpenTtd.ModelArena.Storage;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class RunVerifierTests
{
    [Fact]
    public async Task VerifiesACompleteSealedRun()
    {
        using SealedRun run = await SealedRun.CreateAsync();

        RunVerificationResult result = await RunVerifier.VerifyAsync(run.Directory, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.VerifiedArtifactCount >= 20);
    }

    [Fact]
    public async Task RecalculatesTheScoreFromTheSealedScenarioAndMetrics()
    {
        using SealedRun run = await SealedRun.CreateAsync();

        ScoreRecalculationResult result = await ScoreRecalculator.RecalculateAsync(run.Directory, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(result.StoredScoreSha256, result.RecalculatedScoreSha256);
    }

    [Theory]
    [InlineData(ObservationArtifactWriter.ActionsFileName)]
    [InlineData(BenchmarkArtifactStore.ScoreFileName)]
    [InlineData("final-save.sav")]
    [InlineData(RunManifestFinalizer.ManifestFileName)]
    public async Task DetectsAlteredEvidenceAndManifestArtifacts(string relativePath)
    {
        using SealedRun run = await SealedRun.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(run.Directory, relativePath), "altered", CancellationToken.None);

        RunVerificationResult result = await RunVerifier.VerifyAsync(run.Directory, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ArenaErrorCodes.ArtifactVerificationFailed, result.ErrorCode);
    }

    private sealed class SealedRun : IDisposable
    {
        private readonly TemporaryDirectory _temporaryDirectory;

        private SealedRun(TemporaryDirectory temporaryDirectory, string directory)
        {
            _temporaryDirectory = temporaryDirectory;
            Directory = directory;
        }

        public string Directory { get; }

        public static async Task<SealedRun> CreateAsync()
        {
            TemporaryDirectory temporaryDirectory = new();
            try
            {
                string directory = temporaryDirectory.CreateDirectory("run");
                RunPathPolicy paths = new(directory);
                using ObservationArtifactWriter writer = new(paths, "run-0001");

                paths.CreateDirectory("input");
                paths.CreateDirectory("input/schemas");
                string[] inputPaths =
                [
                    "input/starting-save.sav",
                    "input/content-manifest.json",
                    "input/game-settings.cfg",
                    "input/scenario.yaml",
                    "input/schemas/scenario.v1.json",
                    "input/prompt-template.txt",
                    "input/tool-contracts.json",
                    "input/schemas/observation.v1.json",
                    "input/schemas/action-request.v1.json",
                    "input/schemas/score.v1.json",
                    "input/schemas/protocol-envelope.v1.json",
                    "input/retry-policy.json",
                    "input/end-condition.json",
                ];
                string sourceScenarioPath = Path.Combine(FindRepositoryRoot(), "scenarios", "road-profit-smoke-v1.yaml");
                foreach (string relativePath in inputPaths)
                {
                    string contents = string.Equals(relativePath, "input/scenario.yaml", StringComparison.Ordinal)
                        ? await File.ReadAllTextAsync(sourceScenarioPath, CancellationToken.None)
                        : relativePath;
                    await File.WriteAllTextAsync(paths.Resolve(relativePath), contents, CancellationToken.None);
                }

                await File.WriteAllTextAsync(paths.Resolve("final-save.sav"), "synthetic save", CancellationToken.None);
                BenchmarkMetricSnapshot finalMetrics = new()
                {
                    SchemaVersion = ContractVersions.MetricV1,
                    RunId = "run-0001",
                    SampleId = "metric-final-1",
                    Kind = "final",
                    Source = "gamescript",
                    GameDate = "1950-01-01",
                    GameTick = 42,
                    Metrics = new BenchmarkMetrics
                    {
                        Cash = 90_000,
                        Loan = 0,
                        QuarterlyIncome = 10_000,
                        QuarterlyExpenses = 4_000,
                        OperatingProfit = 6_000,
                        CompanyValue = 120_000,
                        QuarterlyCargoDelivered = 250,
                        ActiveVehicleCount = 1,
                        OperationalRouteCount = 1,
                        CompletedProjectCount = 1,
                        InfrastructureInvestment = 20_000,
                        InvalidDecisionCount = 0,
                        ConstraintViolationCount = 0,
                    },
                };
                await BenchmarkArtifactStore.WriteFinalMetricsAsync(paths, finalMetrics, CancellationToken.None);
                await writer.AppendMetricAsync(finalMetrics, CancellationToken.None);
                ScenarioLoadResult scenario = await ScenarioLoader.LoadAsync(
                    directory,
                    paths.Resolve("input/scenario.yaml"),
                    CancellationToken.None);
                Assert.True(scenario.Succeeded);
                ScoreResult score = new RoadProfitScoreCalculator().Calculate(new ScoreInput(
                    scenario.Document!.Scenario,
                    finalMetrics,
                    []));
                await BenchmarkArtifactStore.WriteScoreAsync(paths, score, CancellationToken.None);

                Dictionary<string, string> hashes = inputPaths.ToDictionary(
                    path => path,
                    path => ComputeSha256(paths.Resolve(path)),
                    StringComparer.Ordinal);
                BenchmarkInputHashes inputHashes = new()
                {
                    StartingSaveSha256 = hashes["input/starting-save.sav"],
                    ContentManifestSha256 = hashes["input/content-manifest.json"],
                    ScenarioSha256 = hashes["input/scenario.yaml"],
                    ScenarioSchemaSha256 = hashes["input/schemas/scenario.v1.json"],
                    GameSettingsSha256 = hashes["input/game-settings.cfg"],
                    PromptTemplateSha256 = hashes["input/prompt-template.txt"],
                    ToolContractSha256 = hashes["input/tool-contracts.json"],
                    ObservationSchemaSha256 = hashes["input/schemas/observation.v1.json"],
                    ActionSchemaSha256 = hashes["input/schemas/action-request.v1.json"],
                    ScoreSchemaSha256 = hashes["input/schemas/score.v1.json"],
                    ProtocolSchemaSha256 = hashes["input/schemas/protocol-envelope.v1.json"],
                    RetryPolicySha256 = hashes["input/retry-policy.json"],
                    EndConditionSha256 = hashes["input/end-condition.json"],
                };
                string[] artifacts =
                [
                    .. inputPaths,
                    ObservationArtifactWriter.ObservationsFileName,
                    ObservationArtifactWriter.GameEventsFileName,
                    ObservationArtifactWriter.DecisionsFileName,
                    ObservationArtifactWriter.ProviderUsageFileName,
                    ObservationArtifactWriter.ActionsFileName,
                    ObservationArtifactWriter.MetricsFileName,
                    BenchmarkArtifactStore.FinalMetricsFileName,
                    BenchmarkArtifactStore.ScoreFileName,
                    "final-save.sav",
                ];
                await RunManifestFinalizer.FinalizeAsync(
                    paths,
                    new RunManifestDraft(
                        "run-0001",
                        DateTimeOffset.Parse("2026-07-25T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                        "0.7.0",
                        new string('a', 40),
                        "replay",
                        "road-profit-replay-v1",
                        "road-profit-smoke",
                        "1.0.0",
                        new ContractVersionsUsed
                        {
                            Protocol = ContractVersions.ProtocolV1,
                            Observation = ContractVersions.ObservationV1,
                            Action = ContractVersions.ActionV1,
                            Goal = ContractVersions.ScenarioV1,
                            Score = ContractVersions.ScoreV1,
                            Manifest = ContractVersions.RunManifestV1,
                        },
                        inputHashes),
                    artifacts,
                    CancellationToken.None);
                return new SealedRun(temporaryDirectory, directory);
            }
            catch
            {
                temporaryDirectory.Dispose();
                throw;
            }
        }

        public void Dispose() => _temporaryDirectory.Dispose();

        private static string ComputeSha256(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? directory = new(System.IO.Directory.GetCurrentDirectory());
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "OpenTTD.ModelArena.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("The test repository root is unavailable.");
        }
    }
}
