namespace Nlabs.ClaudeCodeVsPackage.Bridge.Agent;

/// <summary>
/// What the panel appends to Claude's own system prompt, with <c>--append-system-prompt</c>.
///
/// The failure this exists for is not wrong code - it is code that compiles and does not run. An
/// entity added and never migrated, a service written and never registered, a package referenced in
/// one project and not the one that uses it, a connection string that names a database nobody
/// created. Every one of those builds clean, and every one of them is found by the developer a
/// minute later instead of by the agent a second earlier.
///
/// The extension is the one place that can say so, because it is the one that knows a build
/// succeeded. So the closing instruction is not "be careful": it is to say plainly what was not
/// checked. An honest "I did not run this" is worth more than a confident summary, and it is the
/// sentence a model will otherwise leave out.
/// </summary>
public static class DefinitionOfDone
{
    /// <summary>The text appended to the system prompt for every panel session.</summary>
    public const string Text =
        "Compiling is not the same as working. Before you call a change done, check the things " +
        "that build cleanly and still fail at run time: a model change that has no migration, a " +
        "service or handler that is never registered in the container, a package referenced in " +
        "one project but missing from the one that uses it, a configuration or connection string " +
        "that points at something that does not exist, an endpoint that is written but never " +
        "mapped. Prefer checking to assuming: you can build the solution, run the tests and read " +
        "the diagnostics from here. Then finish by saying, in one line, what you did NOT verify - " +
        "name it rather than leaving it unsaid.";
}
