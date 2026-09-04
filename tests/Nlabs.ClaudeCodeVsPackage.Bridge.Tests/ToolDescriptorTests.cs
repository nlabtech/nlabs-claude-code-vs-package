using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

public class ToolDescriptorTests
{
    private static readonly ToolDescriptor ProposeEdit =
        new ToolDescriptor("propose_edit", "Propose an edit to the active file as a diff.");

    [Fact]
    public void With_no_errors_the_description_is_unchanged()
    {
        var context = new BridgeContext { CompileErrorCount = 0 };

        var description = ProposeEdit.DescriptionFor(context);

        Assert.Equal(ProposeEdit.BaseDescription, description);
    }

    [Fact]
    public void With_errors_a_hint_is_pushed_into_the_description()
    {
        var context = new BridgeContext { CompileErrorCount = 3 };

        var description = ProposeEdit.DescriptionFor(context);

        Assert.StartsWith(ProposeEdit.BaseDescription, description);
        Assert.Contains("3 compile error", description);
        Assert.Contains("resolve these before", description);
    }
}
