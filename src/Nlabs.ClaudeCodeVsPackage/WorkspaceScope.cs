namespace Nlabs.ClaudeCodeVsPackage;

/// <summary>
/// The folder the developer chose in the panel, which the IDE tools count as part of the workspace.
///
/// With no solution open it is the only root the tools can be scoped to - and that is exactly the
/// case openSolution exists for: Claude creates a solution inside the chosen folder, then opens it.
/// The panel owns the choice and the tool catalog only reads it; there is one per Visual Studio
/// process, like the panel itself.
/// </summary>
internal static class WorkspaceScope
{
    private static volatile string? _chosenFolder;

    public static string? ChosenFolder
    {
        get => _chosenFolder;
        set => _chosenFolder = string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
