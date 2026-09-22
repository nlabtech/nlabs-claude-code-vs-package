using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using System.Collections.Generic;
using System.IO;

namespace Nlabs.ClaudeCodeVsPackage;

/// <summary>
/// What counts as the workspace: the open solution's folder, each project's folder (a project can
/// live outside the solution's), and the folder chosen in the panel. Top-level projects only; one
/// nested in a solution folder is still covered when it sits under the solution, as it nearly
/// always does.
///
/// It lives on its own because two very different things ask the same question - the tool catalog,
/// before it opens a file for Claude, and the selection watcher, before it pushes what the
/// developer has highlighted. If each kept its own answer the two would drift, and the one that
/// drifted would be the one nobody was watching.
/// </summary>
internal static class WorkspaceRoots
{
    public static List<string> Collect(DTE2 dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var roots = new List<string>();
        try
        {
            Solution? solution = dte.Solution;
            if (solution != null && solution.IsOpen && !string.IsNullOrEmpty(solution.FullName))
            {
                string? dir = Path.GetDirectoryName(solution.FullName);
                if (!string.IsNullOrEmpty(dir)) roots.Add(dir!);

                foreach (Project project in solution.Projects)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(project.FullName)) continue;
                        string? projectDir = Path.GetDirectoryName(project.FullName);
                        if (!string.IsNullOrEmpty(projectDir)) roots.Add(projectDir!);
                    }
                    catch { /* an unloaded project has no path to give */ }
                }
            }
        }
        catch { /* no solution, or DTE is busy: the panel's folder alone is the scope */ }

        string? chosen = WorkspaceScope.ChosenFolder;
        if (!string.IsNullOrEmpty(chosen)) roots.Add(chosen!);
        return roots;
    }
}
