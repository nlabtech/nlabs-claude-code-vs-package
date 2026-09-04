using System;

namespace Nlabs.ClaudeCodeVsPackage.Bridge;

public enum ApplyMode
{
    /// <summary>Show a diff, change nothing. The safe fallback.</summary>
    DiffOnly,

    /// <summary>Actually write the change to the file.</summary>
    Apply
}

/// <summary>
/// Safe-by-default resolution of agent requests.
///
/// A model that is eager to "help" - or a garbled/partial request - must never fall
/// through to the destructive path. The default is always the harmless one: only an
/// explicit, recognized opt-in escalates. Absent, empty, or unrecognized input resolves
/// to DiffOnly (show, do not write), never to Apply. The user's work is safe unless they
/// clearly asked otherwise.
/// </summary>
public static class SafeDefaults
{
    public static ApplyMode ResolveApplyMode(string? requested)
    {
        return string.Equals(requested, "apply", StringComparison.OrdinalIgnoreCase)
            ? ApplyMode.Apply
            : ApplyMode.DiffOnly;
    }
}
