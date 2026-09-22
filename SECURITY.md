# Security

This extension runs an AI agent inside your IDE, with your files and your shell. That is the
whole point of it, and it is also the reason this page exists: what it can reach, what stops it,
and what is deliberately left to you.

## Reporting a vulnerability

Please report privately through GitHub's **[Report a vulnerability]**
(https://github.com/nlabtech/nlabs-claude-code-vs-package/security/advisories/new) form rather
than opening a public issue. Include what you did, what happened, and the version from
`Extensions > Manage Extensions`.

This is an independent project maintained in spare time. Expect a first reply within a week. A
confirmed report is fixed in the next release, and the advisory credits you unless you would
rather it did not.

## What it does not do

- **No network listener beyond loopback.** The bridge and the approval endpoint both bind
  `127.0.0.1` only, on ports the OS hands out. Nothing is reachable from the network.
- **No telemetry, no analytics, no crash reporting.** Nothing leaves the machine except the
  conversation you have with Claude, over the CLI's own connection and your own account.
- **No model access of its own.** There is no API key in the extension and no service behind it.
  It drives the `claude` CLI you installed and signed in yourself.
- **No third-party runtime dependencies.** Two assemblies ship in the VSIX; the JSON library is
  the one Visual Studio already loads.
- **No symbols in Release.** A shipped DLL does not carry the path of the machine that built it.

## Trust boundaries

Three of them, and each has a different answer.

### 1. Anyone who can talk to the loopback ports

Any process running as you can reach a loopback port. So both endpoints require a **per-session
token**, compared in constant time, generated fresh at startup and never logged. The bridge's
token is written only to `~/.claude/ide/<port>.lock`, a user-scoped file; the approval endpoint's
is passed to the CLI through the environment.

Both reject any request carrying an `Origin` header - that is a browser, and a browser has no
business here - and any `Host` that is not loopback. The bridge takes one client at a time and
drops messages over 1 MB. The approval endpoint answers only `POST /permission`, reads at most
1 MB, and denies once too many calls are waiting rather than growing without limit.

The lock file holds the port, the token, the process id and the open workspace folders. Nothing
about the machine, the account or the subscription.

### 2. The model, deciding to do something you did not ask for

A model that has read a file may have read an instruction inside it. Treat every tool call as
something that may not have come from you.

- **Editing and shell tools go through a PreToolUse hook.** Before the CLI runs one, it asks the
  panel, and the turn blocks on your answer. The call is shown with its real arguments - an edit
  as a line diff, a command on a prompt line - and graded low / medium / high.
- **If the hook cannot reach the panel, it answers deny.** Failing closed is the point; an
  approval bridge that fails open is worse than none, because you would not know.
- **Beneath every mode sits a permission floor** the CLI itself enforces, written into the
  `--settings` it is started with: destructive shell commands (`rm`, `sudo`, force push, hard
  reset) and reads of local secrets (`.env`, `*.pem`, `id_rsa`, `.npmrc`, and the rest).
- **Claude never writes your buffer.** A change arrives as a diff; saving it accepts, closing it
  rejects. You remain the only writer of record.

**Hands off mode asks nothing.** It is there because sometimes you want it, and the floor still
holds, but it is a mode you choose - not one you arrive in.

### 3. The IDE tools, which do not go through the hook

The hook's matcher covers the CLI's own tools, not the twenty-eight this extension adds - so
nothing asks before `openFile` or `getCurrentSelection` runs. Two of them together would
otherwise read any file on the machine, including the ones the floor denies to `Read`. A floor is
only as strong as the easiest way around it, so every tool that opens a file checks first:

- **Inside the workspace**, meaning the open solution, its projects and the panel's working
  folder. With no workspace known, nothing is allowed: "unknown" must never read as "anywhere".
- **Not a secret**, by the same list the floor uses, so the two cannot drift apart.
- **Where the path really goes.** A junction is a directory any user can create without admin
  rights, so a folder inside the workspace can point at `C:\Users\you\.ssh` and read as local
  either way; a link can give a secret a name the list does not know. Paths are resolved through
  the file system and judged again on what comes back.

The same check gates what the editor pushes on its own: a selection in a file outside the
workspace, or in a secret, is not reported.

## What is left to you

- **A command you approve, runs.** The panel shows you the command; reading it is the control.
- **Anything already in your workspace is in scope.** That is what a workspace means here. A
  `.env` is refused by name, but a secret in a file with an ordinary name is an ordinary file.
- **Hands off mode is yours to choose**, and so is a `permissions.allow` rule in your own
  `~/.claude/settings.json`. A rule with a wildcard in the middle of a command approves a family
  of commands rather than one - worth reading twice.
- **The CLI, the model and your account are not this extension.** Their security is Anthropic's
  and yours; this page covers what happens between Visual Studio and the CLI.

## Supported versions

The latest release. This is early-stage software developed in public - try it before relying on
it in production.
