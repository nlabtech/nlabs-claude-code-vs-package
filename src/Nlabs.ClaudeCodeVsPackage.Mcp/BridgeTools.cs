using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace Nlabs.ClaudeCodeVsPackage.Mcp;

/// <summary>
/// The tools Claude Code sees. Each forwards to the Visual Studio extension over the local
/// bridge and returns the JSON reply. Names mirror Claude Code's built-in IDE tools so the
/// model treats them as familiar; build and debugger control go beyond that set - Visual
/// Studio's edge. Descriptions are the control surface: they say when to reach for each tool.
/// Mutations follow the single-writer rule (propose; VS applies), never touching disk here.
/// </summary>
[McpServerToolType]
public sealed class BridgeTools
{
    private readonly BridgeClient _bridge;

    public BridgeTools(BridgeClient bridge) => _bridge = bridge;

    // --- read-only context ---

    [McpServerTool(Name = "getDiagnostics")]
    [Description("Get Error List diagnostics (errors and warnings) from Visual Studio. Optionally filter by file path. Call before proposing changes to see what is already broken.")]
    public Task<string> GetDiagnostics(
        [Description("Optional absolute file path to filter by; omit for the whole solution.")] string? path,
        CancellationToken ct)
        => ForwardAsync("getDiagnostics", new JsonObject { ["path"] = path }, ct);

    [McpServerTool(Name = "getCurrentSelection")]
    [Description("Get the active document path and the developer's current selection (text, line, column).")]
    public Task<string> GetCurrentSelection(CancellationToken ct)
        => ForwardAsync("getCurrentSelection", null, ct);

    [McpServerTool(Name = "getOpenEditors")]
    [Description("List the documents currently open in Visual Studio and whether each has unsaved changes.")]
    public Task<string> GetOpenEditors(CancellationToken ct)
        => ForwardAsync("getOpenEditors", null, ct);

    [McpServerTool(Name = "getWorkspaceFolders")]
    [Description("Get the open solution file and its project folders.")]
    public Task<string> GetWorkspaceFolders(CancellationToken ct)
        => ForwardAsync("getWorkspaceFolders", null, ct);

    [McpServerTool(Name = "readFile")]
    [Description("Read the contents of a file on disk by absolute path.")]
    public Task<string> ReadFile(
        [Description("Absolute path of the file to read.")] string path,
        CancellationToken ct)
        => ForwardAsync("readFile", new JsonObject { ["path"] = path }, ct);

    [McpServerTool(Name = "checkDocumentDirty")]
    [Description("Check whether a document has unsaved changes. Omit path for the active document.")]
    public Task<string> CheckDocumentDirty(
        [Description("Optional absolute file path; omit for the active document.")] string? path,
        CancellationToken ct)
        => ForwardAsync("checkDocumentDirty", new JsonObject { ["path"] = path }, ct);

    [McpServerTool(Name = "getDebugState")]
    [Description("Get the debugger state: whether execution is stopped in break mode and, if so, the current function.")]
    public Task<string> GetDebugState(CancellationToken ct)
        => ForwardAsync("getDebugState", null, ct);

    [McpServerTool(Name = "getSolutionStructure")]
    [Description("List the solution's projects and their source files.")]
    public Task<string> GetSolutionStructure(CancellationToken ct)
        => ForwardAsync("getSolutionStructure", null, ct);

    [McpServerTool(Name = "findSymbols")]
    [Description("Search the whole solution for declared symbols (types, methods, properties) by name, using Roslyn. Returns each match's name, kind, file and line.")]
    public Task<string> FindSymbols(
        [Description("The symbol name (or partial name) to search for.")] string query,
        CancellationToken ct)
        => ForwardAsync("findSymbols", new JsonObject { ["query"] = query }, ct);

    [McpServerTool(Name = "findReferences")]
    [Description("Find all references to the symbol at a given file, line and column, using Roslyn.")]
    public Task<string> FindReferences(
        [Description("Absolute path of the source file.")] string file,
        [Description("1-based line of the symbol.")] int line,
        [Description("1-based column of the symbol.")] int column,
        CancellationToken ct)
        => ForwardAsync("findReferences", new JsonObject { ["file"] = file, ["line"] = line, ["column"] = column }, ct);

    // --- tests / version control ---

    [McpServerTool(Name = "runTests")]
    [Description("Run 'dotnet test' for the solution and return the result. Optionally pass a test filter expression.")]
    public Task<string> RunTests(
        [Description("Optional test filter (dotnet --filter syntax); omit to run all tests.")] string? filter,
        CancellationToken ct)
        => ForwardAsync("runTests", new JsonObject { ["filter"] = filter }, ct);

    [McpServerTool(Name = "gitStatus")]
    [Description("Get the working tree status of the solution's git repository (porcelain).")]
    public Task<string> GitStatus(CancellationToken ct)
        => ForwardAsync("gitStatus", null, ct);

    // --- debugger depth ---

    [McpServerTool(Name = "getCallStack")]
    [Description("Get the current call stack while stopped in the debugger (functions and languages).")]
    public Task<string> GetCallStack(CancellationToken ct)
        => ForwardAsync("getCallStack", null, ct);

    [McpServerTool(Name = "evaluateExpression")]
    [Description("Evaluate an expression in the current debugger context (break mode). Returns value and type.")]
    public Task<string> EvaluateExpression(
        [Description("The expression to evaluate, e.g. a variable name.")] string expression,
        CancellationToken ct)
        => ForwardAsync("evaluateExpression", new JsonObject { ["expression"] = expression }, ct);

    [McpServerTool(Name = "listBreakpoints")]
    [Description("List the current breakpoints (file, line, enabled, condition).")]
    public Task<string> ListBreakpoints(CancellationToken ct)
        => ForwardAsync("listBreakpoints", null, ct);

    [McpServerTool(Name = "removeBreakpoint")]
    [Description("Remove the breakpoint at a file and line.")]
    public Task<string> RemoveBreakpoint(
        [Description("Absolute path of the source file.")] string file,
        [Description("1-based line of the breakpoint to remove.")] int line,
        CancellationToken ct)
        => ForwardAsync("removeBreakpoint", new JsonObject { ["file"] = file, ["line"] = line }, ct);

    // --- navigation / editor state ---

    [McpServerTool(Name = "openFile")]
    [Description("Open a file in Visual Studio, optionally navigating to a line.")]
    public Task<string> OpenFile(
        [Description("Absolute path of the file to open.")] string path,
        [Description("Optional 1-based line to navigate to.")] int? line,
        CancellationToken ct)
        => ForwardAsync("openFile", new JsonObject { ["path"] = path, ["line"] = line }, ct);

    [McpServerTool(Name = "saveDocument")]
    [Description("Save a document. Omit path to save the active document.")]
    public Task<string> SaveDocument(
        [Description("Optional absolute file path; omit for the active document.")] string? path,
        CancellationToken ct)
        => ForwardAsync("saveDocument", new JsonObject { ["path"] = path }, ct);

    [McpServerTool(Name = "closeTab")]
    [Description("Close the editor tab for the given file path.")]
    public Task<string> CloseTab(
        [Description("Absolute path of the open file whose tab to close.")] string path,
        CancellationToken ct)
        => ForwardAsync("closeTab", new JsonObject { ["path"] = path }, ct);

    [McpServerTool(Name = "formatDocument")]
    [Description("Format a document with Visual Studio's formatter. Omit path for the active document.")]
    public Task<string> FormatDocument(
        [Description("Optional absolute file path; omit for the active document.")] string? path,
        CancellationToken ct)
        => ForwardAsync("formatDocument", new JsonObject { ["path"] = path }, ct);

    // --- single-writer mutation ---

    [McpServerTool(Name = "openDiff")]
    [Description("Propose new content for a file. Visual Studio shows it as a diff (current vs proposed); the developer is the only one who applies it. Never writes the file directly.")]
    public Task<string> OpenDiff(
        [Description("Absolute path of the file to change.")] string file,
        [Description("The full proposed content of the file.")] string proposedText,
        CancellationToken ct)
        => ForwardAsync("openDiff", new JsonObject { ["file"] = file, ["proposedText"] = proposedText }, ct);

    // --- build (beyond the built-in IDE tool set) ---

    [McpServerTool(Name = "buildSolution")]
    [Description("Build the current solution and return whether it succeeded and how many projects failed.")]
    public Task<string> BuildSolution(CancellationToken ct)
        => ForwardAsync("buildSolution", null, ct);

    // --- debugger control (Visual Studio's edge) ---

    [McpServerTool(Name = "addBreakpoint")]
    [Description("Add a breakpoint at a file and line.")]
    public Task<string> AddBreakpoint(
        [Description("Absolute path of the source file.")] string file,
        [Description("1-based line number for the breakpoint.")] int line,
        CancellationToken ct)
        => ForwardAsync("addBreakpoint", new JsonObject { ["file"] = file, ["line"] = line }, ct);

    [McpServerTool(Name = "debugControl")]
    [Description("Control the debugger. action is one of: continue, stepOver, stepInto, stepOut, break, stop.")]
    public Task<string> DebugControl(
        [Description("One of: continue, stepOver, stepInto, stepOut, break, stop.")] string action,
        CancellationToken ct)
        => ForwardAsync("debugControl", new JsonObject { ["action"] = action }, ct);

    private async Task<string> ForwardAsync(string type, JsonObject? payload, CancellationToken ct)
    {
        JsonNode? reply = await _bridge.SendAsync(type, payload, ct);
        return reply?.ToJsonString() ?? "{}";
    }
}
