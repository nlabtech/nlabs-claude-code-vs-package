using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

namespace Nlabs.ClaudeCodeVsPackage
{
    /// <summary>
    /// Answers "which line are we on?" correctly while debugging.
    ///
    /// The trap: the editor caret (where the user's cursor sits) is NOT the execution
    /// line. When a session is stopped at a breakpoint, the developer may scroll or click
    /// anywhere - the caret moves, the execution point does not. An agent that reports the
    /// caret as "current line" gives Claude Code the wrong location. The execution line
    /// must come from the DEBUGGER's active stack frame, not the text view.
    /// </summary>
    internal static class DebugState
    {
        /// <summary>The editor caret line (1-based). Where the user is looking - not execution.</summary>
        public static bool TryGetCaretLine(IVsTextManager textManager, out int line)
        {
            line = 0;
            if (textManager == null) return false;

            if (textManager.GetActiveView(1, null, out IVsTextView view) != VSConstants.S_OK || view == null)
                return false;
            if (view.GetCaretPos(out int caretLine, out _) != VSConstants.S_OK)
                return false;

            line = caretLine + 1; // text views are 0-based
            return true;
        }

        /// <summary>
        /// The execution line (1-based) and file, taken from the debugger's stack frame.
        /// This is the real "current line" while stopped; the caret is irrelevant here.
        /// </summary>
        public static bool TryGetExecutionLine(IDebugStackFrame2 frame, out string? file, out int line)
        {
            file = null;
            line = 0;
            if (frame == null) return false;

            if (frame.GetDocumentContext(out IDebugDocumentContext2 context) != VSConstants.S_OK || context == null)
                return false;

            var begin = new TEXT_POSITION[1];
            var end = new TEXT_POSITION[1];
            if (context.GetStatementRange(begin, end) != VSConstants.S_OK)
                return false;

            context.GetName(enum_GETNAME_TYPE.GN_FILENAME, out file);
            line = (int)begin[0].dwLine + 1; // debugger positions are 0-based
            return true;
        }
    }
}
