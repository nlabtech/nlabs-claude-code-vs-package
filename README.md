# Claude Code for Visual Studio (nLabtech)

A bridge extension that connects the solution open in Visual Studio to the Claude
Code CLI. Your selected code, open files, and compiler errors reach Claude
automatically - without leaving Visual Studio.

> **Independent / community project.** Not officially affiliated with Anthropic or
> Microsoft. "Claude Code", "Anthropic" and "Visual Studio" are trademarks of their
> respective owners. This extension began as a personal need: Claude Code has no
> official Visual Studio support, so I built one for myself.

## How this repo grows

This extension is built step by step, in public. Each meaningful step ships alongside
a post; the matching commit contains the exact code discussed in that post. So the
lesson and the working code always live in the same place.

Posts: [@turkmvc](https://www.linkedin.com/in/turkmvc/) - nLabtech ([nlabtech.com.tr](https://nlabtech.com.tr))

## What it does

- **Selected code becomes context on its own** - no need to name the file or line.
- **Reads compiler errors from Visual Studio** - the Error List.
- **Shows changes in Visual Studio's own diff window** - accept or reject.
- Runs no model, writes no files, sends no telemetry; listens only on `127.0.0.1`
  and uses your own Claude subscription on your own machine.

## Technology

- SDK-style VSSDK extension, in-process, targeting `net472` - the Visual Studio shell
  still runs on .NET Framework 4.7.2.
- A single VSIX installs on both Visual Studio 2022 and 2026 (`InstallationTarget [17.0,)`).
- The core (WebSocket server, handshake, protocol) lives in a project with zero
  dependency on Visual Studio - so it can be tested without opening VS.

## Status

Early stage, developed in public. Try at your own risk before using in production.

## License

MIT - see [LICENSE](LICENSE).
