# Changelog

All notable changes to this extension. Versions follow the manifest.

## 0.2.0

### Added

- **Edits are shown as a diff.** A pending `Edit` used to arrive as pretty-printed tool
  input, which puts the old and the new text in two quoted blobs with the newlines
  escaped - the least readable form of the most consequential decision in the panel.
  Each call now appears in the shape it has: an edit as a line diff with unchanged runs
  folded away, a command on a prompt line that wraps rather than scrolls, a new file as
  its content, a lookup as the path it names.
- **Tables, links, quotes, rules and task lists** in replies. Markdown that Claude
  writes constantly - a comparison table above all - was being shown as its own source.
- **Warm dictation.** The speech engine now starts when recording begins, so the model
  loads while you are still speaking instead of afterwards, and stays loaded for next
  time. It also uses the GPU when the machine has one.
- **A model download is asked for, not assumed.** The panel names the model, says
  roughly what it weighs and that transcription then happens locally, and shows the
  download's progress rather than sitting silent for minutes.
- Built packages are copied to `artifacts/`, stamped with version and configuration.
- **`goToDefinition`** - resolves a symbol by name, or at a position the way F12 does, and
  returns the declaration's own source, so Claude can read a definition without reading the
  whole file it lives in. Asked to, it also shows it in the editor.
- **`openSolution`** - opens a `.sln` or `.slnx`, so the build, diagnostics and symbol tools
  work on a solution Claude has just created. Only inside the workspace, and never over a
  solution with unsaved changes.
- **`clearBreakpoints`** - removes every breakpoint, or only those in one file.

### Fixed

- **A language switch no longer clears the conversation.** The feed was replayed from
  the stored messages, and only messages are stored - so tool chips, subagent cards,
  turn footers, attached images and any approval still waiting were destroyed. The
  waiting approval was the worst of it: the hook was left hanging until it timed out.
- **Subagents are named again.** The delegation tool ships under two different names
  across CLI versions and only one was matched, so every line a subagent produced showed
  up as an anonymous "subagent".
- **The IDE is discoverable after you open a solution.** The discovery file was written
  once at load - and Visual Studio usually starts with no solution open, so nothing
  matched. It now follows the solution as it opens and closes.

### Security

- **IDE tools stay inside the workspace, and away from secrets.** The IDE tools do not pass
  through the approval step, and two of them together - opening a file with a line range
  selected, then reading the selection - could read any file on the machine, including the
  `.env` and key files the permission floor denies to the CLI's own `Read`. Tools that open
  a file now refuse anything outside the open solution, its projects and the panel's working
  folder, and anything the floor names as a secret.
- Release builds no longer carry the path of the machine that built them. The compiler
  writes the symbol file's full path into the assembly by default, readable by anyone
  who downloads the extension; Release now emits no symbol file at all.

## 0.1.0

- First working version: agentic panel driving the Claude Code CLI, native IDE bridge
  with Visual Studio tools (editor, solution, symbols, build, debugger), risk-graded
  approvals, conversation history, dictation, and token and rate-limit reporting.
