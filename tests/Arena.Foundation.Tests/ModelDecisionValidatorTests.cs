using OpenTtd.ModelArena.Contracts;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class ModelDecisionValidatorTests
{
    [Fact]
    public void RejectsPrivateReasoningAndUnknownFields()
    {
        ModelDecisionValidationResult result = ModelDecisionValidator.ParseAndValidate(
            """
            {
              "decision_id": "decision-0001",
              "public_summary": "Build a route.",
              "observations": ["A viable pair is available."],
              "actions": [{"tool": "wait", "arguments": {"game_days": 7}}],
              "next_review_game_days": 7,
              "chain_of_thought": "private"
            }
            """,
            RoadToolCatalog.All.ToHashSet(StringComparer.Ordinal));

        Assert.False(result.IsValid);
        Assert.Equal(ArenaErrorCodes.ProviderSchemaMismatch, result.Error?.Code);
    }

    [Fact]
    public void ClassifiesMalformedJsonSeparatelyFromSchemaMismatch()
    {
        ModelDecisionValidationResult result = ModelDecisionValidator.ParseAndValidate(
            "{not-json}",
            RoadToolCatalog.All.ToHashSet(StringComparer.Ordinal));

        Assert.False(result.IsValid);
        Assert.Equal(ArenaErrorCodes.ProviderInvalidJson, result.Error?.Code);
    }

    [Fact]
    public void ReturnsTheSharedTypedDecisionWhenTheContractIsValid()
    {
        ModelDecisionValidationResult result = ModelDecisionValidator.ParseAndValidate(
            """
            {
              "decision_id": "decision-0001",
              "public_summary": "Wait for demand to accumulate.",
              "observations": ["Town demand is still low."],
              "actions": [{"tool": "wait", "arguments": {"game_days": 7}}],
              "next_review_game_days": 7
            }
            """,
            new HashSet<string>(StringComparer.Ordinal) { RoadToolCatalog.Wait });

        Assert.True(result.IsValid);
        Assert.Equal("decision-0001", result.Decision?.DecisionId);
        Assert.Equal(RoadToolCatalog.Wait, Assert.Single(result.Decision!.Actions).Tool);
    }

    [Fact]
    public void EnforcesTheScenarioDeclaredActionLimit()
    {
        ModelDecisionValidationResult result = ModelDecisionValidator.ParseAndValidate(
            """
            {
              "decision_id": "decision-0001",
              "public_summary": "Take two review steps.",
              "observations": ["The scenario allows only one action."],
              "actions": [
                {"tool": "wait", "arguments": {"game_days": 7}},
                {"tool": "wait", "arguments": {"game_days": 14}}
              ],
              "next_review_game_days": 7
            }
            """,
            new HashSet<string>(StringComparer.Ordinal) { RoadToolCatalog.Wait },
            maximumActions: 1);

        Assert.False(result.IsValid);
        Assert.Equal(ArenaErrorCodes.ProviderSchemaMismatch, result.Error?.Code);
    }
}
