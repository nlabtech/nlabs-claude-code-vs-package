# Claude Code for Visual Studio (nLabtech)

A bridge that connects **Claude Code** to **Visual Studio**. Claude sees what Visual
Studio sees - your selection, open files, compiler errors, the debugger - and proposes
changes as diffs you approve. It runs entirely on your machine and uses your own Claude
subscription.

> **Independent / community project.** Not officially affiliated with Anthropic or
> Microsoft. "Claude Code", "Anthropic" and "Visual Studio" are trademarks of their
> respective owners. This began as a personal need: Claude Code has no official Visual
> Studio support, so I built one.

## How it works

The extension is discovered as a **native IDE**. It does not add an MCP server and there is
no `claude mcp add` step - instead it advertises itself the way Claude Code's own IDE
integrations do, and the CLI connects with `/ide`.

```
Claude Code  ──/ide──▶  ~/.claude/ide/<port>.lock  ──WebSocket 127.0.0.1 (MCP)──▶  VS extension  ──▶  Visual Studio
```

- On load (with a solution open) the extension starts a loopback WebSocket bridge and writes a
  discovery **lock file** to `~/.claude/ide/<port>.lock` - the port, a session token, this
  process id and the open workspace folders. Nothing about the machine, account or
  subscription is written.
- `claude` finds that file, connects over the socket and speaks the **Model Context Protocol**
  directly. Because the extension is a first-class `/ide` connection, its tools appear to the
  model **without an `mcp__` prefix** - `openDiff`, `getDiagnostics`, `openFile` resolve to
  Visual Studio.
- **Bridge core** (`Nlabs.ClaudeCodeVsPackage.Bridge`): the WebSocket server, the native
  handshake, the hand-written MCP protocol and the `claude -p` stream-json engine - all with
  zero dependency on Visual Studio, so they are unit-tested without opening VS.
- **VS extension** (`Nlabs.ClaudeCodeVsPackage`): the in-process VSSDK package that owns the
  bridge, maps each tool call to a Visual Studio action, and hosts the agentic panel.

## Tools

The built-in IDE tools are matched by name and shape, so Visual Studio answers them exactly as
Claude Code expects. Beyond them sit Visual Studio's own edge - build, tests, the debugger and
Roslyn - capabilities the standard IDE bridge does not carry.

| Group | Tools |
|-------|-------|
| **Context (read-only)** | `getDiagnostics`, `getCurrentSelection`, `getLatestSelection`, `getOpenEditors`, `getWorkspaceFolders`, `checkDocumentDirty` |
| **Navigation / editor** | `openFile`, `saveDocument`, `close_tab`, `closeAllDiffTabs`, `formatDocument` |
| **Change (single-writer)** | `openDiff` |
| **Build / tests / VCS** | `buildSolution`, `runTests`, `gitStatus` |
| **Solution** | `getSolutionStructure`, `openSolution` |
| **Symbols (Roslyn)** | `findSymbols`, `findReferences`, `goToDefinition` |
| **Debugger** | `getDebugState`, `getCallStack`, `evaluateExpression`, `listBreakpoints`, `addBreakpoint`, `removeBreakpoint`, `clearBreakpoints`, `debugControl` (continue / stepOver / stepInto / stepOut / break / stop) |

**Single-writer principle.** Claude never edits your buffer directly. `openDiff` shows a diff
(current vs proposed) and waits for your verdict - save it to accept (the file is written and
`FILE_SAVED` is reported) or close it to reject (`DIFF_REJECTED`, nothing is written). You stay
the only writer of record.

**Pushing context.** The extension also pushes to Claude the way a native IDE does:
`Tools ▸ Claude Code (nLabtech) ▸ Send Selection to Claude Code` (or the editor right-click)
sends the current selection as an `at_mentioned` notification, so the model picks up what you
are pointing at.

## The agentic panel

`Tools ▸ Claude Code (nLabtech) ▸ Open Claude Panel` opens a dockable chat that drives Claude
Code headlessly (`claude -p`, stream-json) in the open solution's directory - or in a folder you
pick, when no solution is open. It is plain WPF: no embedded browser, and nothing is bundled
into the VSIX beyond the extension's own two assemblies.

The session starts with `--ide`, so the panel is the bridge's client: the tools above are on its
table too, and the selection you are pointing at reaches it without being asked for. The bridge
takes one client at a time - if a terminal already holds it, the panel runs without the editor's
half rather than fighting over it.

### You decide before anything runs

The plain CLI has no channel a client can use to answer a permission prompt - that lives only in
the Agent SDK. So the panel registers a **PreToolUse hook**: before Claude runs an editing or
shell tool, the CLI calls the hook, the hook asks the panel over a loopback endpoint, and the
turn waits on your answer.

- The call is **graded for risk** (low / medium / high) and shown with its **real arguments** -
  an edit as a line diff, a command on a prompt line, a new file as its content.
- **Allow**, **deny**, or allow that tool for the rest of the session. When a turn touches a
  dozen files, one click can answer everything still waiting.
- Three levels decide how often you are asked: **normal** (asks before anything that changes
  files or runs a command, waves through ordinary reads), **full control** (asks before every
  call, reads included) and **hands off** (asks nothing).
- If the endpoint cannot be reached, the hook answers **deny**. Failing closed is the point.

Underneath sits a **permission floor** that no mode lifts, written into the settings
`claude -p` is started with, so the CLI itself enforces it: destructive shell commands
(`rm`, `sudo`, force push, hard reset) and reads of local secrets (`.env`, `*.pem`, `id_rsa`).

### What a turn looks like

Replies stream in as markdown - headings, lists, tables, links, quotes, task lists, and code in
a selectable monospace block with syntax colour. Around them:

- **Tool chips** in the order the calls happened, each one saying what it did.
- **Thinking**, when the model reasons out loud, folded into a quiet block of its own.
- **Subagents** named and indented under the turn that delegated to them.
- A **live task list** as the agent's todos change.
- A **working strip** - elapsed time and tokens - while the turn runs.
- A **footer per turn**: input, output, reasoning and cached tokens, and what it cost. The
  session total is one click away.

### Undo a turn

Before each turn the working tree is snapshotted with `git stash create` - a dangling commit
that touches neither your working tree, your index nor the stash list. **Undo turn** restores
the tracked files that changed back to that snapshot, after you confirm. It never deletes, so a
file Claude created is left for you to remove.

### The rest of it

- **Slash menu.** Type `/` for a searchable list. The panel's own: `new`, `clear`, `model`,
  `effort`, `permission`, `language`, `agents`, `attach`, `folder`, `review`, `undo`. Handed
  straight to the CLI: `init`, `compact`, `context`, `cost`, `memory`, `mcp`, `todos`,
  `status`, `doctor`, `help`.
- **File mentions.** Type `@` to complete a path from the workspace.
- **Attachments.** Paste, drag and drop, or the `+` button; images ride along with the turn.
- **Dictation.** Record, and the text lands in the composer. No speech engine is bundled - you
  name the command in Options, and the engine is kept warm between dictations so the model
  loads while you are still talking.
- **Selectors**, sitting next to Send: model (default, or the latest Opus / Sonnet / Haiku /
  Fable), permission mode (ask each time / accept edits / plan / bypass) and reasoning effort
  (low / medium / high / xhigh / max).
- **Usage.** How full the 5-hour and 7-day windows are, and when they reset.
- **Conversations persist.** Each one keeps its session id, so switching back resumes it
  (`--resume`); they survive restarting Visual Studio.
- **Turkish and English**, switchable live, plus an accent colour. Everything else follows
  Visual Studio's own theme.

## Settings

`Tools ▸ Options ▸ Claude Code` holds only what the panel cannot - machine-level things the
panel has no business duplicating:

| Setting | Default |
|---------|---------|
| **Claude CLI path** | empty - found on `PATH` |
| **Mask secrets in the panel** | on - keys and tokens are redacted where they are displayed |
| **Speech-to-text command** | empty - dictation off until you name an engine |
| **Start the local bridge on load** | on - turn off to keep the extension panel-only |

## Getting started

**Requirements:** Visual Studio 2022 or 2026, and the `claude` CLI on your `PATH` (signed in
with your own subscription).

1. **Build & install the extension.** Open `Nlabs.ClaudeCodeVsPackage.slnx`, build, and press
   F5 to launch it in the Experimental instance (or install the built `.vsix`).
2. **Open a solution.** The bridge starts and the discovery lock file is written automatically.
3. **Connect.** In a terminal, run `claude`, then `/ide` - pick **Visual Studio**. Now ask
   Claude to read diagnostics, propose a diff, set a breakpoint, run the tests, and so on.
4. **Or use the panel.** `Tools ▸ Claude Code (nLabtech) ▸ Open Claude Panel`.

`Tools ▸ Claude Code (nLabtech) ▸ Restart Local Bridge` restarts the bridge if you need it.

## Security

- Listens only on `127.0.0.1`; not reachable from the network.
- Requires a per-session token, presented in the IDE authorization header and compared in
  constant time; it is written only to the lock file (user-scoped) and never logged. The
  approval endpoint the hook talks to has the same posture and its own per-run token.
- Rejects any request carrying an `Origin` header (browser defense) and any non-loopback `Host`.
- Single client at a time; messages larger than 1 MB are dropped.
- **The IDE tools stay inside the workspace.** They do not pass through the approval hook, so
  every tool that opens a file first checks the path against the open solution, its projects
  and the panel's working folder - and refuses a file the permission floor names as a secret,
  wherever it sits. A floor is only as strong as the easiest way around it.
- Runs no model and sends no telemetry. Every change goes through Visual Studio's own edit path,
  behind a diff you approve; tool results carry only what was asked for - no machine, account or
  token detail.

## Building & testing

```bash
# Bridge core unit tests (no Visual Studio needed)
dotnet test tests/Nlabs.ClaudeCodeVsPackage.Bridge.Tests
```

The `.vsix` builds with the Visual Studio toolchain (VSSDK, `net472` - the runtime the VS shell
itself uses); a single package installs on both Visual Studio 2022 and 2026
(`InstallationTarget [17.0,)`).

## How this repo grew

Built step by step, in public. Each step shipped with a post, and the matching commit holds the
exact code discussed - so the lesson and the working code live in the same place.

Posts: [@turkmvc](https://www.linkedin.com/in/turkmvc/) · nLabtech ([nlabtech.com.tr](https://nlabtech.com.tr))

## Status

Early stage, developed in public. Try it before relying on it in production.

## License

MIT - see [LICENSE](LICENSE).
