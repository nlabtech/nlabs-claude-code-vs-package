using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Nlabs.ClaudeCodeVsPackage.Bridge.Agent;

namespace Nlabs.ClaudeCodeVsPackage.AgentPanel;

/// <summary>
/// The agentic panel: a chat surface that drives Claude Code headlessly through
/// <see cref="ClaudeCliSession"/>. The developer types a turn, the panel spawns (or reuses) a
/// <c>claude -p</c> session in the open solution's directory, streams the reply as it arrives and
/// shows the turn's cost.
///
/// It is built in plain WPF (no embedded browser): a Visual Studio tool window is WPF already, so
/// this adds no bundled runtime and cannot clash with the shell's own components - the panel simply
/// paints with the current theme's brushes. Rich markdown/code rendering is a later enhancement.
///
/// The extension never holds an API key: the session runs on the developer's own Claude
/// subscription through the CLI. Nothing about the machine or account is shown or logged here.
/// </summary>
internal sealed class AgentPanelControl : UserControl
{
    private readonly StackPanel _messages;
    private readonly ScrollViewer _scroller;
    private readonly TextBox _input;
    private readonly TextBlock _status;

    private ClaudeCliSession? _session;
    private TextBlock? _streamingReply;
    private string _streamingText = string.Empty;

    public AgentPanelControl()
    {
        this.SetResourceReference(BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
        this.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);

        _messages = new StackPanel { Margin = new Thickness(8) };
        _scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _messages,
        };

        _status = new TextBlock
        {
            Margin = new Thickness(10, 4, 10, 4),
            Opacity = 0.7,
            Text = "Claude Code (nLabtech) - type a message and press Enter.",
        };
        _status.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

        _input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinLines = 1,
            MaxLines = 6,
            Margin = new Thickness(8),
            Padding = new Thickness(6),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _input.SetResourceReference(TextBox.BackgroundProperty, VsBrushes.ComboBoxBackgroundKey);
        _input.SetResourceReference(TextBox.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        _input.PreviewKeyDown += OnInputKeyDown;

        var root = new DockPanel();
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(_input, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(_input);
        root.Children.Add(_scroller);
        Content = root;
    }

    // Enter sends; Shift+Enter inserts a newline.
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private async System.Threading.Tasks.Task SendAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // establish the UI thread
        string text = _input.Text?.Trim() ?? string.Empty;
        if (text.Length == 0) return;

        _input.Clear();
        AddBubble(text, isUser: true);

        try
        {
            EnsureSession();
            _streamingText = string.Empty;
            _streamingReply = AddBubble(string.Empty, isUser: false);
            await _session!.SendAsync(text);
        }
        catch (Exception ex)
        {
            _status.Text = "Could not reach the Claude CLI: " + ex.Message;
        }
    }

    // Creates the CLI session the first time, in the open solution's directory.
    private void EnsureSession()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_session != null) return;

        _session = new ClaudeCliSession();
        _session.Event += OnCliEvent;
        _session.Exited += (_, __) => OnUi(() => _status.Text = "Session ended.");
        _session.Start(SolutionDirectory(), new ClaudeCliOptions());
        _status.Text = "Connected.";
    }

    // CLI events arrive on the pump thread; marshal every UI change to the dispatcher.
    private void OnCliEvent(object sender, CliEvent e)
    {
        switch (e.Kind)
        {
            case CliEventKind.SystemInit:
                if (!string.IsNullOrEmpty(e.Model)) OnUi(() => _status.Text = "Model: " + e.Model);
                break;

            case CliEventKind.StreamDelta:
                if (!string.IsNullOrEmpty(e.Text))
                {
                    _streamingText += e.Text;
                    OnUi(() => { if (_streamingReply != null) _streamingReply.Text = _streamingText; ScrollToEnd(); });
                }
                break;

            case CliEventKind.Assistant:
                if (!string.IsNullOrEmpty(e.Text))
                {
                    _streamingText = e.Text!;
                    OnUi(() => { if (_streamingReply != null) _streamingReply.Text = _streamingText; ScrollToEnd(); });
                }
                break;

            case CliEventKind.Result:
                OnUi(() =>
                {
                    _streamingReply = null;
                    if (e.TotalCostUsd.HasValue)
                    {
                        _status.Text = e.IsError
                            ? "Turn failed."
                            : string.Format("Ready - last turn ${0:0.0000}.", e.TotalCostUsd.Value);
                    }
                });
                break;
        }
    }

    private TextBlock AddBubble(string text, bool isUser)
    {
        var content = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 6, 10, 6),
        };
        content.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

        var bubble = new Border
        {
            Child = content,
            CornerRadius = new System.Windows.CornerRadius(8),
            Margin = new Thickness(0, 4, 0, 4),
            MaxWidth = 620,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        bubble.SetResourceReference(Border.BackgroundProperty,
            isUser ? VsBrushes.ComboBoxBackgroundKey : VsBrushes.ToolWindowBackgroundKey);
        bubble.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
        bubble.BorderThickness = new Thickness(1);

        _messages.Children.Add(bubble);
        ScrollToEnd();
        return content;
    }

    private void ScrollToEnd() => _scroller.ScrollToEnd();

    // CLI events arrive on the pump thread; this marshals each update to the panel's own WPF
    // dispatcher. Dispatcher.Invoke is the right tool for a control's thread affinity - it is not a
    // Visual Studio service call, so the shell threading rule does not apply.
#pragma warning disable VSTHRD001
    private void OnUi(Action action) => Dispatcher.Invoke(action);
#pragma warning restore VSTHRD001

    private static string SolutionDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (Package.GetGlobalService(typeof(DTE)) is DTE2 dte && dte.Solution != null &&
                !string.IsNullOrEmpty(dte.Solution.FullName))
            {
                return System.IO.Path.GetDirectoryName(dte.Solution.FullName) ?? string.Empty;
            }
        }
        catch { /* fall through to empty */ }
        return string.Empty;
    }

    public void ShutDown()
    {
        _session?.Dispose();
        _session = null;
    }
}
