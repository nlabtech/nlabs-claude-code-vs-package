namespace Nlabs.ClaudeCodeVsPackage.Bridge;

/// <summary>A snapshot of solution state the bridge knows and the agent does not.</summary>
public sealed class BridgeContext
{
    public bool HasCompileErrors => CompileErrorCount > 0;
    public int CompileErrorCount { get; set; }
}

/// <summary>
/// A tool exposed to the agent (Claude Code) over the bridge.
///
/// The lesson learned the hard way: exposing a tool is not enough. The tool's
/// DESCRIPTION is a control surface, not documentation - it is what decides whether the
/// agent reaches for the tool at the right moment. A "pushed hint" injects a contextual
/// nudge into that description, so the agent is steered by state it could not otherwise
/// see (here: unresolved compile errors) instead of guessing.
/// </summary>
public sealed class ToolDescriptor
{
    public ToolDescriptor(string name, string baseDescription)
    {
        Name = name;
        BaseDescription = baseDescription;
    }

    public string Name { get; }
    public string BaseDescription { get; }

    /// <summary>The description the agent actually receives, with the hint pushed in.</summary>
    public string DescriptionFor(BridgeContext context)
    {
        if (context != null && context.HasCompileErrors)
        {
            return BaseDescription +
                $" NOTE: the solution currently has {context.CompileErrorCount} compile error(s); " +
                "resolve these before proposing new features.";
        }

        return BaseDescription;
    }
}
