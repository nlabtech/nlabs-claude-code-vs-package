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
| **Debugger** | `getDebugState`, `getCallStack`, `evaluateExpression`, `listBreakpoints`, `addBreakpoint`, `removeBreakpoint`, `debugControl` (continue / stepOver / stepInto / stepOut / break / stop) |
| **Navigate the code** | `getSolutionStructure`, `findSymbols`, `findReferences` (Roslyn) |

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
Code headlessly (`claude -p`, stream-json) in the open solution's directory. Pick the model and
the permission mode, type a turn, and the reply streams in with the turn's cost. It is plain WPF
- no embedded browser, nothing bundled into the VSIX beyond the extension's own two assemblies.

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
  constant time; it is written only to the lock file (user-scoped) and never logged.
- Rejects any request carrying an `Origin` header (browser defense) and any non-loopback `Host`.
- Single client at a time; messages larger than 1 MB are dropped.
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
