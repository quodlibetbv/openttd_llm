using OpenTtd.ModelArena.Contracts;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class RoadToolPromptCatalogTests
{
    [Fact]
    public void PublishesOnlyTheAllowedTypedBuildRouteContract()
    {
        IReadOnlyDictionary<string, ModelToolContract> contracts = RoadToolPromptCatalog.CreateAllowedContracts(
            [RoadToolCatalog.BuildTransportRoute]);

        KeyValuePair<string, ModelToolContract> contract = Assert.Single(contracts);
        Assert.Equal(RoadToolCatalog.BuildTransportRoute, contract.Key);
        Assert.Equal(
            ["mode", "source_town_id", "destination_town_id", "cargo", "initial_vehicle_count", "maximum_budget"],
            contract.Value.Arguments.Select(argument => argument.Name));
        Assert.Equal(["road"], contract.Value.Arguments[0].AllowedValues);
        Assert.Equal(
            "observation.sections.constraints_and_budgets.available_project_budget",
            contract.Value.Arguments[^1].MaximumSource);
    }

    [Fact]
    public void RejectsUnknownToolMetadata()
    {
        Assert.Throws<ArgumentException>(() => RoadToolPromptCatalog.CreateAllowedContracts(["unknown_tool"]));
    }
}
