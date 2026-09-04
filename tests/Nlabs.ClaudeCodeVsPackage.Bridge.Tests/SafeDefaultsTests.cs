using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

public class SafeDefaultsTests
{
    [Theory]
    [InlineData("apply")]
    [InlineData("APPLY")]
    [InlineData("Apply")]
    public void Explicit_recognized_optin_escalates_to_apply(string requested)
    {
        Assert.Equal(ApplyMode.Apply, SafeDefaults.ResolveApplyMode(requested));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("yes")]
    [InlineData("apply-now")]
    [InlineData("diff")]
    public void Absent_or_unrecognized_input_stays_safe(string? requested)
    {
        Assert.Equal(ApplyMode.DiffOnly, SafeDefaults.ResolveApplyMode(requested));
    }
}
