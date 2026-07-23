using OpenTtd.ModelArena.Storage;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class CredentialReferenceTests
{
    [Fact]
    public void ParsesAWindowsCredentialManagerReferenceWithoutASecretValue()
    {
        bool parsed = CredentialReference.TryParse(
            "credman:OpenTTDModelArena/OBS",
            out CredentialReference? reference);

        Assert.True(parsed);
        Assert.NotNull(reference);
        Assert.True(reference.IsArenaManaged);
        Assert.Equal("OpenTTDModelArena/OBS", reference.Target);
    }

    [Theory]
    [InlineData("OpenTTDModelArena/OBS")]
    [InlineData("credman:")]
    [InlineData("credman: OpenTTDModelArena/OBS")]
    public void RejectsValuesThatAreNotSafeCredentialReferences(string value)
    {
        Assert.False(CredentialReference.TryParse(value, out _));
    }

    [Fact]
    public void ManagedCredentialCommandsCannotAddressArbitraryWindowsTargets()
    {
        Assert.True(CredentialReference.IsArenaManagedTarget("OpenTTDModelArena/DeepSeek"));
        Assert.False(CredentialReference.IsArenaManagedTarget("OtherApplication/Secret"));
        Assert.False(CredentialReference.IsArenaManagedTarget("OpenTTDModelArena/nested/name"));
        Assert.False(CredentialReference.IsArenaManagedTarget("OpenTTDModelArena/contains space"));
        Assert.False(CredentialReference.IsArenaManagedTarget("OpenTTDModelArena/caf\u00e9"));
    }

    [Theory]
    [InlineData("credman:OpenTTDModelArena/nested/name")]
    [InlineData("credman:OpenTTDModelArena/contains space")]
    [InlineData("credman:OpenTTDModelArena/caf\u00e9")]
    public void ParsedReferencesOutsideTheManagedTargetPolicyAreNotArenaManaged(string value)
    {
        Assert.True(CredentialReference.TryParse(value, out CredentialReference? reference));
        Assert.NotNull(reference);
        Assert.False(reference.IsArenaManaged);
    }
}
