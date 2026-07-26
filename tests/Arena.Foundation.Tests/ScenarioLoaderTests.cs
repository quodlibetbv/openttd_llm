using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Orchestrator;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class ScenarioLoaderTests
{
    [Fact]
    public async Task LoadsTheCheckedInSmokeScenarioAndMatchesItsPublishedFingerprint()
    {
        string repositoryRoot = FindRepositoryRoot();
        string scenarioPath = Path.Combine(repositoryRoot, "scenarios", "road-profit-smoke-v1.yaml");

        ScenarioLoadResult loaded = await ScenarioLoader.LoadAsync(repositoryRoot, scenarioPath, CancellationToken.None);
        ScenarioPublicationCatalog catalog = await ScenarioPublicationRegistry.LoadAsync(
            repositoryRoot,
            ScenarioPublicationRegistry.DefaultRelativePath,
            CancellationToken.None);
        ScenarioPublicationResult publication = ScenarioPublicationRegistry.RequirePublished(loaded.Document!, catalog);

        Assert.True(loaded.Succeeded);
        Assert.NotNull(loaded.Document);
        Assert.Equal(ContractVersions.ScenarioV1, loaded.Document.Scenario.SchemaVersion);
        Assert.Equal("road-profit-smoke", loaded.Document.Scenario.ScenarioId);
        Assert.True(publication.Succeeded);
    }

    [Fact]
    public async Task RejectsUnknownScenarioFieldsAndPublishedContentMutation()
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourcePath = Path.Combine(repositoryRoot, "scenarios", "road-profit-smoke-v1.yaml");
        using TemporaryDirectory directory = new();
        string scenarioPath = directory.WriteFile(
            "scenarios/road-profit-smoke-v1.yaml",
            File.ReadAllText(sourcePath) + Environment.NewLine + "unexpected_field: rejected" + Environment.NewLine);

        ScenarioLoadResult invalid = await ScenarioLoader.LoadAsync(directory.Path, scenarioPath, CancellationToken.None);
        Assert.False(invalid.Succeeded);
        Assert.Contains(invalid.Errors, error => string.Equals(error.Field, "root.unexpected_field", StringComparison.Ordinal));

        ScenarioLoadResult valid = await ScenarioLoader.LoadAsync(repositoryRoot, sourcePath, CancellationToken.None);
        Assert.True(valid.Succeeded);
        ScenarioPublicationCatalog mutatedCatalog = new()
        {
            SchemaVersion = ContractVersions.ScenarioV1,
            PublishedScenarios =
            [
                new PublishedScenarioEntry
                {
                    ScenarioId = valid.Document!.Scenario.ScenarioId,
                    Version = valid.Document.Scenario.Version,
                    Sha256 = new string('0', 64),
                },
            ],
        };

        ScenarioPublicationResult publication = ScenarioPublicationRegistry.Validate(valid.Document!, mutatedCatalog);

        Assert.False(publication.Succeeded);
        Assert.Equal(ArenaErrorCodes.ScenarioPublicationConflict, publication.ErrorCode);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
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
