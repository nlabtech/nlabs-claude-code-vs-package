using System;
using System.Text.RegularExpressions;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Agent;

/// <summary>How much damage a tool call could do if it is wrong.</summary>
public enum RiskLevel
{
    /// <summary>Reads something; cannot change the machine.</summary>
    Low,
    /// <summary>Changes a file or editor state, reversibly.</summary>
    Medium,
    /// <summary>Runs a shell command, or does something hard to undo.</summary>
    High,
}

/// <summary>A risk verdict for one tool call: the level and a short why.</summary>
public sealed class RiskAssessment
{
    public RiskAssessment(RiskLevel level, string reason)
    {
        Level = level;
        Reason = reason;
    }

    public RiskLevel Level { get; }
    public string Reason { get; }
}

/// <summary>
/// Grades a pending tool call so the approval card can say what is actually at stake. An approval
/// prompt that looks identical for "list the open files" and "rm -rf" trains people to click Allow
/// without reading; grading restores the signal.
///
/// The bias is deliberate: unknown tools are NOT treated as safe, and a shell command is only
/// downgraded from High when nothing about it looks destructive. Pure and dependency-free, so the
/// rules are unit-tested.
/// </summary>
public static class RiskAssessor
{
    // Tools that only observe. Names cover both the CLI's own tools and this extension's IDE tools.
    private static readonly string[] ReadOnlyTools =
    {
        "Read", "Glob", "Grep", "NotebookRead", "TodoWrite",
        "getDiagnostics", "getOpenEditors", "getCurrentSelection", "getLatestSelection",
        "getWorkspaceFolders", "checkDocumentDirty", "getSolutionStructure",
        "findSymbols", "findReferences", "gitStatus", "listBreakpoints", "getDebugState", "getCallStack",
        "goToDefinition",
    };

    // Tools that change a file or editor state, but reversibly and visibly.
    private static readonly string[] WriteTools =
    {
        "Write", "Edit", "NotebookEdit", "openDiff", "openFile", "saveDocument", "formatDocument",
        "close_tab", "closeAllDiffTabs", "addBreakpoint", "removeBreakpoint", "clearBreakpoints",
        "ExitPlanMode",
    };

    // goToDefinition only reads - unless it is also asked to show the result, which moves the editor.
    private static readonly Regex OpensEditor = new Regex("\"open\"\\s*:\\s*true",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Tools that hand control to a shell or the wider machine.
    private static readonly string[] ShellTools = { "Bash", "PowerShell", "runTests", "buildSolution", "debugControl" };

    // Commands whose blast radius is large or hard to undo. Matched case-insensitively against the
    // call's input; a hit keeps a shell command at High no matter what else it says.
    private static readonly Regex Destructive = new Regex(
        @"\brm\s+-[rf]|\brmdir\b|\bdel\s+/|\bformat\s+[a-z]:|\bmkfs\b|\bdd\s+if=|" +
        @"git\s+push\s+.*--force|git\s+reset\s+--hard|git\s+clean\s+-[a-z]*f|" +
        @"\bdrop\s+(table|database)\b|\btruncate\s+table\b|" +
        @"\bshutdown\b|\breg\s+delete\b|\bRemove-Item\b.*-Recurse|" +
        @"curl\s+[^|]*\|\s*(sh|bash)|iwr\s+[^|]*\|\s*iex",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Grades a tool call from its name and the preview of its input.</summary>
    public static RiskAssessment Assess(string? toolName, string? inputPreview)
    {
        string name = toolName ?? string.Empty;
        string input = inputPreview ?? string.Empty;

        if (Destructive.IsMatch(input))
        {
            return new RiskAssessment(RiskLevel.High, "Looks destructive or hard to undo.");
        }

        // Opening a solution makes Visual Studio evaluate its projects, and a design-time build runs
        // whatever MSBuild logic they carry - code execution, whatever the tool happens to be called.
        if (name == "openSolution")
        {
            return new RiskAssessment(RiskLevel.High, "Loads a solution; its project build logic runs.");
        }

        if (name == "goToDefinition" && OpensEditor.IsMatch(input))
        {
            return new RiskAssessment(RiskLevel.Medium, "Changes a file or the editor.");
        }

        if (Contains(ShellTools, name))
        {
            return new RiskAssessment(RiskLevel.High, "Runs a command on your machine.");
        }

        if (Contains(ReadOnlyTools, name))
        {
            return new RiskAssessment(RiskLevel.Low, "Only reads; changes nothing.");
        }

        if (Contains(WriteTools, name))
        {
            return new RiskAssessment(RiskLevel.Medium, "Changes a file or the editor.");
        }

        // Anything unrecognised is not assumed safe.
        return new RiskAssessment(RiskLevel.Medium, "Unrecognised tool.");
    }

    private static bool Contains(string[] names, string name)
    {
        foreach (string candidate in names)
        {
            if (string.Equals(candidate, name, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
