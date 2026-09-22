# Contributing

This repository is written in public, one step at a time, and each step ships with a post. That
shapes what is easy to take and what is not - so it is worth a page rather than a guess.

## What helps most

**Bug reports from a real session.** What you asked, what the panel did, what you expected. The
version from `Extensions > Manage Extensions`, your Visual Studio version, and `claude --version`.
Most of what breaks here breaks against a particular CLI version.

**A fix with the test that would have caught it.** The bridge core has no dependency on Visual
Studio precisely so its rules can be tested without one.

**A security finding.** Not here - see [SECURITY.md](SECURITY.md). Please do not open a public
issue for one.

Before starting something large, open an issue first. A feature may already be written and
waiting on the post it belongs to, and nobody enjoys finding that out from a pull request.

## Building it

**You need** Visual Studio 2022 or 2026 with the **Visual Studio extension development** workload
(that is what supplies the VSSDK), and the `claude` CLI on your `PATH` to run it.

```bash
# The bridge core: no Visual Studio needed, seconds to run.
dotnet test tests/Nlabs.ClaudeCodeVsPackage.Bridge.Tests
```

For the extension itself, open `Nlabs.ClaudeCodeVsPackage.slnx` and press F5 - it launches the
Experimental instance with the package deployed. The built `.vsix` is also copied to
`artifacts/`, stamped with the version and configuration.

The two projects divide along one line, and it is worth keeping:

- **`Nlabs.ClaudeCodeVsPackage.Bridge`** - the WebSocket server, the MCP protocol, the CLI
  engine, the permission floor and the path rules. No reference to Visual Studio, ever. If a
  rule can live here, it lives here, because here it can be tested.
- **`Nlabs.ClaudeCodeVsPackage`** - the VSSDK package: the tool catalog, the editor and debugger
  plumbing, the WPF panel.

## House style

`.editorconfig` covers the mechanical part. The rest:

- **The build is warning-free, and stays that way.** That includes the VS threading analyzers -
  a VSTHRD warning is usually telling you something true about which thread you are on.
- **Comments say why, not what.** The code already says what. A comment earns its place by
  recording the thing the next reader would otherwise have to rediscover - the reason a check
  is there, the failure that made it necessary.
- **Plain ASCII in source files.** Encoding layers between here and a build server have mangled
  multi-byte characters before.
- **Nullable is enabled** in both projects. Keep it that way rather than silencing it.
- **Nothing sensitive in a log, an error message or a tool result.** No token, no path of the
  machine, no account detail. An error tells the model what to do instead.

## Commits

One change per commit, and a subject line that says what changed for the person using it rather
than which file moved. The body is for why. The repository's history is part of the series, so a
commit that reads well is doing double duty.

Tests come with the commit they belong to, not after it.

## Licence

By contributing you agree that your contribution is licensed under the MIT licence, the same as
the rest of the repository.
