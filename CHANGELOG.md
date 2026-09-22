# Changelog

All notable changes to this extension. Versions follow the manifest.

## 0.4.0

### Added

- **Every tool reaches the model, not just the one the native path allows.** Connected with
  `/ide`, Claude Code surfaces only the IDE tools it already knows - `getDiagnostics`, and
  `openDiff` through the edit flow - so the Roslyn, build, test and debugger tools this extension
  adds were advertised over the socket and then never listed to the model. The same protocol is
  now offered a second way, over a loopback **HTTP MCP endpoint**, where every entry in
  `tools/list` becomes callable. The panel wires it into its own session automatically with
  `--mcp-config`; a terminal adds it once with `claude mcp add`. Tools arrive under an `mcp__vs__`
  name there, and the list-changed notification is pushed over SSE the way the socket path pushes
  it. Same posture as the bridge: loopback only, a per-session bearer token, a one-megabyte
  ceiling, nothing logged.

## 0.3.0

### Added

- **The IDE speaks, instead of only answering.** The bridge could describe the selection when
  asked and never mentioned it otherwise, so asked what the developer was looking at, the
  model scanned the file system and guessed. The editor now pushes `selection_changed` as
  the developer moves: which view has focus comes from Visual Studio's text manager over a
  COM connection point, and the selection inside it from the editor's own WPF view, which
  reports the span rather than only the caret's line. Moving the caret fires on every
  keystroke, so pushes are coalesced and an unchanged selection is dropped.
- **The panel is the IDE's client.** It runs inside Visual Studio and could see none of it:
  started without `--ide`, it never connected to the bridge sitting in the same process, so
  the Visual Studio tools were not on its table and no push ever reached it. Everything the
  extension offers belonged to whoever had typed `/ide` in a terminal - the one place the
  extension is not. The bridge still takes one client at a time; if a terminal holds it, the
  panel carries on without the editor's half rather than fighting over it.
- **A tool's description carries the state the agent cannot see.** `openDiff` - the only tool
  that proposes a change, and so the only one where the nudge is worth its noise - now says
  how many compile errors are standing in the way. `initialize` had advertised
  `tools.listChanged` while nothing ever sent the notification, which meant the client listed
  the tools once at connect and kept that copy; the count is taken where the error list is
  read anyway, and the client is told to look again only when the number moves.
- **A definition of done.** Every session is started with an appended system prompt about the
  failures that compile and still fall over - a model change with no migration, a service
  nobody registered, a package missing from the project that uses it, an endpoint never
  mapped - and is asked to close by naming what it did not verify. Running inside the IDE is
  what makes that fair: the build, the tests and the diagnostics are one tool call away.

### Security

- **The secret list named four files, and it is the list two doors share.** `PathScope`
  derives what an IDE tool may open from the permission floor's own `Read` rules, so every
  name missing from it was a file the CLI's `Read` would fetch and an IDE tool would open: a
  private key that is not `id_rsa`, a `.pfx`, a `.npmrc`, a .NET user-secrets file. Naming
  them in one place closes both.
- **`saveDocument` and `checkDocumentDirty` skipped the workspace check.** That a document has
  to be open already is not a scope rule - a developer opens files from all over the machine -
  and saving writes.
- **The workspace rule was decided on text, and text is not the whole truth.** A junction is a
  directory any user can create without admin rights, so a folder inside the open solution could
  point at `C:\Users\you\.ssh` and read as local either way; a link gives a secret a name the
  secret list does not know. Every path an IDE tool is handed is now resolved through the file
  system and judged again on where it really lands. Resolving is best-effort - a file that does
  not exist yet keeps the verdict it had - so the step can take an answer away but never hand
  one out.
- **The approval endpoint checked the token and then took whatever came.** Any verb, any path,
  and a body read to the end before anything looked at it. It now answers only `POST /permission`,
  stops at a megabyte the way the bridge does, and denies once too many calls are waiting rather
  than holding a slot for each for five minutes.
- **The panel stopped leaving its temp files behind.** A settings file was written per session
  start and never removed, each carrying the permission floor and the path of a script the CLI
  is told to execute, as was the hook script itself.

### Documentation

- The readme described twenty-five of the twenty-eight tools that exist, and gave the panel -
  the approval cards and the floor beneath them, undo, the slash menu, dictation, usage, the
  settings page - a single paragraph saying you pick a model and type.

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
