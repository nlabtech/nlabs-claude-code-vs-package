using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace Nlabs.ClaudeCodeVsPackage.AgentPanel;

/// <summary>
/// The dockable tool window that hosts the agentic panel. Visual Studio creates it on demand
/// (through ShowToolWindowAsync); its content is the WPF <see cref="AgentPanelControl"/>.
/// </summary>
[Guid(ToolWindowGuidString)]
internal sealed class AgentPanelToolWindow : ToolWindowPane
{
    public const string ToolWindowGuidString = "b1e7c4a2-3f56-49d8-9c1a-7e2d5b8f4a61";

    private readonly AgentPanelControl _control;

    public AgentPanelToolWindow() : base(null)
    {
        Caption = "Claude Code (nLabtech)";
        _control = new AgentPanelControl();
        Content = _control;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _control.ShutDown();
        }
        base.Dispose(disposing);
    }
}
