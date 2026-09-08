using Nlabs.ClaudeCodeVsPackage.Bridge.Agent;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

public class SecretMaskerTests
{
    [Theory]
    [InlineData("key is sk-ant-api03-AbCdEfGhIjKlMnOpaaaa done", "sk-ant-***")]
    [InlineData("token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ012345", "ghp_***")]
    [InlineData("aws AKIAIOSFODNN7EXAMPLE here", "AKIA***")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz", "Bearer ***")]
    [InlineData("conn ...;Password=Sup3rSecret!;Database=x", "Password=***")]
    public void Redact_masks_known_secret_shapes(string input, string expectedFragment)
    {
        string masked = SecretMasker.Redact(input);

        Assert.Contains(expectedFragment, masked);
    }

    [Fact]
    public void Redact_masks_a_jwt()
    {
        string masked = SecretMasker.Redact("here eyJhbGciOi.eyJzdWIiOi.SflKxwRJSM is a jwt");

        Assert.Contains("eyJ***", masked);
        Assert.DoesNotContain("SflKxwRJSM", masked);
    }

    [Fact]
    public void Redact_leaves_ordinary_text_untouched()
    {
        const string plain = "Just a normal sentence about openDiff and getDiagnostics.";

        Assert.Equal(plain, SecretMasker.Redact(plain));
    }

    [Fact]
    public void Redact_tolerates_null_and_empty()
    {
        Assert.Equal(string.Empty, SecretMasker.Redact(null));
        Assert.Equal(string.Empty, SecretMasker.Redact(string.Empty));
    }
}
