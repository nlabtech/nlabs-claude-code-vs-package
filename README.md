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

```
Claude Code  ──stdio──▶  MCP server (.NET)  ──WebSocket 127.0.0.1 + token──▶  VS extension  ──▶  Visual Studio
```

- **VS extension** (`Nlabs.ClaudeCodeVsPackage`): an in-process VSSDK package that opens a
  local WebSocket bridge and turns messages into Visual Studio actions.
- **Bridge core** (`Nlabs.ClaudeCodeVsPackage.Bridge`): the WebSocket server, handshake and
  security - zero dependency on Visual Studio, so it is unit-tested without opening VS.
- **MCP server** (`Nlabs.ClaudeCodeVsPackage.Mcp`): a stdio [MCP](https://modelcontextprotocol.io)
  server that Claude Code launches; it exposes the tools below and forwards each to the
  extension over the socket.

Tool names mirror Claude Code's built-in IDE tools, so the model treats them as familiar.

## Tools

| Group | Tools |
|-------|-------|
| **Context (read-only)** | `getDiagnostics`, `getCurrentSelection`, `getOpenEditors`, `getWorkspaceFolders`, `readFile`, `checkDocumentDirty`, `getDebugState` |
| **Navigation / editor** | `openFile`, `saveDocument`, `closeTab`, `formatDocument` |
| **Change (single-writer)** | `openDiff` |
| **Build / tests / VCS** | `buildSolution`, `runTests`, `gitStatus` |
| **Debugger** | `addBreakpoint`, `removeBreakpoint`, `listBreakpoints`, `debugControl` (continue / stepOver / stepInto / stepOut / break / stop), `getCallStack`, `evaluateExpression` |
| **Navigate the code** | `getSolutionStructure`, `findSymbols`, `findReferences` (Roslyn) |

24 tools in all. Build, tests and the debugger go beyond the built-in IDE tool set - that is
Visual Studio's edge.

**Single-writer principle:** Claude never edits your buffer directly. `openDiff` shows a diff
(current vs proposed); you stay the only one who applies a change.

## Getting started

**Requirements:** Visual Studio 2022 or 2026, the .NET 10 SDK (for the MCP server).

1. **Build & install the extension.** Open `Nlabs.ClaudeCodeVsPackage.slnx`, build, and press
   F5 to launch it in the Experimental instance (or install the built `.vsix`).
2. **Start the bridge.** In Visual Studio: **Tools ▸ Restart Local Bridge**. A dialog shows the
   port and a session token.
3. **Build the MCP server.**
   ```bash
   dotnet build src/Nlabs.ClaudeCodeVsPackage.Mcp -c Release
   ```
4. **Register it with Claude Code** (port and token from step 2):
   ```bash
   claude mcp add vs-bridge \
     -e NLABS_BRIDGE_PORT=<port> \
     -e NLABS_BRIDGE_TOKEN=<token> \
     -- dotnet <path>/nlabs-claude-code-bridge-mcp.dll
   ```
5. In Claude Code, the `vs-bridge` tools are now available. Ask Claude to read diagnostics,
   propose a diff, set a breakpoint, and so on.

## Security

- Listens only on `127.0.0.1`; not reachable from the network.
- Requires a 32-byte crypto-random session token, compared in constant time; never logged.
- Rejects any request carrying an `Origin` header (browser defense) and any non-loopback `Host`.
- Single client at a time; messages larger than 1 MB are dropped.
- Runs no model, sends no telemetry, and the MCP process never writes to disk - all changes go
  through Visual Studio's own edit path, behind a diff you approve.

## Building & testing

```bash
# Bridge core unit tests (no Visual Studio needed)
dotnet test tests/Nlabs.ClaudeCodeVsPackage.Bridge.Tests
```

The `.vsix` builds with the Visual Studio toolchain (VSSDK, `net472`); a single package installs
on both Visual Studio 2022 and 2026 (`InstallationTarget [17.0,)`).

## How this repo grew

Built step by step, in public. Each step shipped with a post, and the matching commit holds the
exact code discussed - so the lesson and the working code live in the same place.

Posts: [@turkmvc](https://www.linkedin.com/in/turkmvc/) · nLabtech ([nlabtech.com.tr](https://nlabtech.com.tr))

## Status

Early stage, developed in public. Try it before relying on it in production.

## License

MIT - see [LICENSE](LICENSE).
