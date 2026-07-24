using OpenTtd.ModelArena.Storage;
using Xunit;

namespace OpenTtd.ModelArena.Foundation.Tests;

public sealed class SecretRedactorTests
{
    [Fact]
    public void RedactsCommonCredentialShapesBeforeDoctorOrSetupOutput()
    {
        string token = "sk-" + new string('a', 24);
        string value = $"Authorization: Bearer {token}; password=local-value";

        string redacted = SecretRedactor.Redact(value);

        Assert.DoesNotContain(token, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("local-value", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactsSecretsAndWindowsMachinePathsFromPersistedArtifacts()
    {
        string token = "sk-" + new string('b', 24);
        string value = $"Could not write C:\\Arena\\runs\\private.cfg with Authorization: Bearer {token}";

        string redacted = ArtifactTextRedactor.Redact(value);

        Assert.DoesNotContain("C:\\Arena", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(token, redacted, StringComparison.Ordinal);
        Assert.Contains("[LOCAL-PATH]", redacted, StringComparison.Ordinal);
    }
}
