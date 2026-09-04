using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Nlabs.ClaudeCodeVsPackage.Bridge;

// EnvDTE and Microsoft.CodeAnalysis both define Document/Solution/Project. The editor-state
// handlers mean the EnvDTE ones; the Roslyn equivalents are only ever used through 'var'.
using Document = EnvDTE.Document;
using Solution = EnvDTE.Solution;
using Project = EnvDTE.Project;

// Every handler below is reached only through HandleAsync, which switches to the main thread
// before dispatching, so all DTE access here is on the UI thread. The threading analyzer
// cannot follow that guarantee across the await into the handler methods, so its UI-thread
// warnings are suppressed for this file; the invariant is enforced at the single entry point.
#pragma warning disable VSTHRD010

namespace Nlabs.ClaudeCodeVsPackage
{
    /// <summary>
    /// Turns bridge messages into Visual Studio actions and sends replies back.
    ///
    /// This is the piece that makes the parts one working extension: it owns the server's
    /// MessageReceived event, parses the { id, type, payload } envelope, dispatches to a
    /// handler on the UI thread, and answers { id, ok, result|error }.
    ///
    /// Tool/message names mirror Claude Code's built-in IDE tools (openDiff, getDiagnostics,
    /// getCurrentSelection, openFile, getOpenEditors, getWorkspaceFolders...) so the model
    /// treats them as familiar. Build and debugger control go BEYOND the built-in set - that
    /// is Visual Studio's edge. Every mutation follows the single-writer rule: the agent
    /// proposes, VS applies; the MCP process never writes to disk itself.
    /// </summary>
    internal sealed class BridgeRouter
    {
        private readonly BridgeServer _server;
        private readonly JoinableTaskFactory _jtf;
        private readonly IAsyncServiceProvider _services;
        private readonly DiffSession _diff;
        private readonly JobRegistry _jobs;

        public BridgeRouter(BridgeServer server, JoinableTaskFactory jtf, IAsyncServiceProvider services)
        {
            _server = server;
            _jtf = jtf;
            _services = services;
            _diff = new DiffSession(services);
            _jobs = new JobRegistry();

            _server.MessageReceived += OnMessage;
        }

        private void OnMessage(object? sender, string json)
        {
            _ = _jtf.RunAsync(async () =>
            {
                string reply;
                try
                {
                    reply = await HandleAsync(json);
                }
                catch (Exception ex)
                {
                    reply = Error(null, ex.Message);
                }

                await _server.SendAsync(reply);
            });
        }

        private async Task<string> HandleAsync(string json)
        {
            JObject message;
            try
            {
                message = JObject.Parse(json);
            }
            catch
            {
                return Error(null, "invalid JSON");
            }

            string? id = (string?)message["id"];
            string? type = (string?)message["type"];
            var payload = message["payload"] as JObject ?? new JObject();

            // Everything below touches DTE, which lives on the UI thread.
            await _jtf.SwitchToMainThreadAsync();
            var dte = await _services.GetServiceAsync(typeof(DTE)) as DTE2;
            if (dte == null)
            {
                return Error(id, "DTE unavailable");
            }

            switch (type)
            {
                case "ping": return Ok(id, new JObject { ["pong"] = true });

                // --- read-only context ---
                case "getDiagnostics": return GetDiagnostics(id, dte, (string?)payload["path"]);
                case "getCurrentSelection": return GetCurrentSelection(id, dte);
                case "getOpenEditors": return GetOpenEditors(id, dte);
                case "getWorkspaceFolders": return GetWorkspaceFolders(id, dte);
                case "readFile": return ReadFile(id, (string?)payload["path"]);
                case "checkDocumentDirty": return CheckDocumentDirty(id, dte, (string?)payload["path"]);
                case "getDebugState": return GetDebugState(id, dte);
                case "getSolutionStructure": return GetSolutionStructure(id, dte);
                case "findSymbols": return await FindSymbolsAsync(id, (string?)payload["query"]);
                case "findReferences": return await FindReferencesAsync(id, (string?)payload["file"], (int?)payload["line"], (int?)payload["column"]);

                // --- debugger depth ---
                case "getCallStack": return GetCallStack(id, dte);
                case "evaluateExpression": return EvaluateExpression(id, dte, (string?)payload["expression"]);
                case "listBreakpoints": return ListBreakpoints(id, dte);
                case "removeBreakpoint": return RemoveBreakpoint(id, dte, (string?)payload["file"], (int?)payload["line"]);

                // --- navigation / editor state ---
                case "openFile": return OpenFile(id, dte, (string?)payload["path"], (int?)payload["line"]);
                case "saveDocument": return SaveDocument(id, dte, (string?)payload["path"]);
                case "closeTab": return CloseTab(id, dte, (string?)payload["path"]);
                case "formatDocument": return FormatDocument(id, dte, (string?)payload["path"]);

                // --- single-writer mutation ---
                case "openDiff": return await OpenDiffAsync(id, (string?)payload["file"], (string?)payload["proposedText"]);

                // --- build / tests / vcs (beyond the built-in set) ---
                case "buildSolution": return BuildSolution(id, dte);
                case "runTests": return await RunTestsAsync(id, dte, (string?)payload["filter"]);
                case "gitStatus": return await GitStatusAsync(id, dte);

                // --- debugger control (Visual Studio's edge) ---
                case "addBreakpoint": return AddBreakpoint(id, dte, (string?)payload["file"], (int?)payload["line"]);
                case "debugControl": return DebugControl(id, dte, (string?)payload["action"]);

                default: return Error(id, "unknown message type: " + (type ?? "<null>"));
            }
        }

        // --- read-only ---

        private string GetDiagnostics(string? id, DTE2 dte, string? path)
        {
            var items = dte.ToolWindows.ErrorList.ErrorItems;
            var diagnostics = new JArray();
            for (int i = 1; i <= items.Count; i++)
            {
                ErrorItem item = items.Item(i);
                if (!string.IsNullOrEmpty(path) &&
                    !string.Equals(item.FileName, path, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                diagnostics.Add(new JObject
                {
                    ["file"] = item.FileName,
                    ["line"] = item.Line,
                    ["column"] = item.Column,
                    ["description"] = item.Description,
                    ["level"] = (int)item.ErrorLevel,
                });
            }

            return Ok(id, new JObject { ["diagnostics"] = diagnostics });
        }

        private string GetCurrentSelection(string? id, DTE2 dte)
        {
            var doc = dte.ActiveDocument;
            if (doc == null) return Error(id, "no active document");

            var result = new JObject { ["path"] = doc.FullName };
            if (doc.Selection is TextSelection selection)
            {
                result["selectedText"] = selection.Text;
                result["line"] = selection.CurrentLine;
                result["column"] = selection.CurrentColumn;
            }

            return Ok(id, result);
        }

        private string GetOpenEditors(string? id, DTE2 dte)
        {
            var editors = new JArray();
            foreach (Document doc in dte.Documents)
            {
                editors.Add(new JObject
                {
                    ["path"] = doc.FullName,
                    ["saved"] = doc.Saved,
                });
            }

            return Ok(id, new JObject { ["openEditors"] = editors });
        }

        private string GetWorkspaceFolders(string? id, DTE2 dte)
        {
            var folders = new JArray();
            Solution? solution = dte.Solution;
            if (solution != null)
            {
                foreach (Project project in solution.Projects)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(project.FullName))
                        {
                            folders.Add(Path.GetDirectoryName(project.FullName));
                        }
                    }
                    catch
                    {
                        // solution folders have no FullName; skip
                    }
                }
            }

            return Ok(id, new JObject
            {
                ["solution"] = solution?.FullName,
                ["folders"] = folders,
            });
        }

        private string ReadFile(string? id, string? path)
        {
            if (string.IsNullOrEmpty(path)) return Error(id, "path is required");
            if (!File.Exists(path)) return Error(id, "file not found");

            return Ok(id, new JObject
            {
                ["path"] = path,
                ["content"] = File.ReadAllText(path),
            });
        }

        private string CheckDocumentDirty(string? id, DTE2 dte, string? path)
        {
            Document? doc = FindDocument(dte, path) ?? dte.ActiveDocument;
            if (doc == null) return Error(id, "no matching document");

            return Ok(id, new JObject
            {
                ["path"] = doc.FullName,
                ["dirty"] = !doc.Saved,
            });
        }

        private string GetDebugState(string? id, DTE2 dte)
        {
            Debugger debugger = dte.Debugger;
            bool inBreak = debugger != null && debugger.CurrentMode == dbgDebugMode.dbgBreakMode;
            var result = new JObject { ["inBreakMode"] = inBreak };

            // Execution context comes from the debugger's stack frame, never the caret.
            if (inBreak && debugger!.CurrentStackFrame != null)
            {
                result["function"] = debugger.CurrentStackFrame.FunctionName;
            }

            return Ok(id, result);
        }

        private string GetSolutionStructure(string? id, DTE2 dte)
        {
            var projects = new JArray();
            Solution? solution = dte.Solution;
            if (solution != null)
            {
                foreach (Project project in solution.Projects)
                {
                    string? fullName = null;
                    try { fullName = project.FullName; } catch { /* solution folder */ }

                    var files = new JArray();
                    try { CollectFiles(project.ProjectItems, files, 0); } catch { /* ignore */ }

                    projects.Add(new JObject
                    {
                        ["name"] = project.Name,
                        ["path"] = fullName,
                        ["files"] = files,
                    });
                }
            }

            return Ok(id, new JObject { ["projects"] = projects });
        }

        // Walk project items a few levels deep, collecting file paths (bounded to stay cheap).
        private static void CollectFiles(ProjectItems? items, JArray sink, int depth)
        {
            if (items == null || depth > 8 || sink.Count >= 1000) return;

            foreach (ProjectItem item in items)
            {
                if (sink.Count >= 1000) return;
                try
                {
                    if (item.FileCount > 0)
                    {
                        string file = item.FileNames[1]; // 1-based
                        if (!string.IsNullOrEmpty(file) && File.Exists(file))
                        {
                            sink.Add(file);
                        }
                    }
                }
                catch { /* some items expose no file */ }

                CollectFiles(item.ProjectItems, sink, depth + 1);
            }
        }

        // Roslyn symbol search across the solution - a capability the built-in IDE tools lack.
        private async Task<string> FindSymbolsAsync(string? id, string? query)
        {
            if (string.IsNullOrEmpty(query)) return Error(id, "query is required");

            var componentModel = await _services.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            var workspace = componentModel?.GetService<VisualStudioWorkspace>();
            var solution = workspace?.CurrentSolution;
            if (solution == null) return Error(id, "no Roslyn workspace");

            var found = await SymbolFinder.FindSourceDeclarationsAsync(solution, query!, ignoreCase: true);

            var symbols = new JArray();
            foreach (var symbol in found.Take(50))
            {
                var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
                int line = location != null ? location.GetLineSpan().StartLinePosition.Line + 1 : 0;
                symbols.Add(new JObject
                {
                    ["name"] = symbol.ToDisplayString(),
                    ["kind"] = symbol.Kind.ToString(),
                    ["file"] = location?.SourceTree?.FilePath,
                    ["line"] = line,
                });
            }

            return Ok(id, new JObject { ["symbols"] = symbols });
        }

        // Find all references to the symbol at a file/line/column, using Roslyn.
        private async Task<string> FindReferencesAsync(string? id, string? file, int? line, int? column)
        {
            if (string.IsNullOrEmpty(file) || !line.HasValue || !column.HasValue)
            {
                return Error(id, "file, line and column are required");
            }

            var componentModel = await _services.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            var workspace = componentModel?.GetService<VisualStudioWorkspace>();
            var solution = workspace?.CurrentSolution;
            if (solution == null) return Error(id, "no Roslyn workspace");

            var documentId = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault();
            var document = documentId != null ? solution.GetDocument(documentId) : null;
            if (document == null) return Error(id, "file is not part of the solution");

            var text = await document.GetTextAsync();
            if (line.Value < 1 || line.Value > text.Lines.Count) return Error(id, "line out of range");
            int position = text.Lines[line.Value - 1].Start + Math.Max(0, column.Value - 1);

            var semanticModel = await document.GetSemanticModelAsync();
            var root = await document.GetSyntaxRootAsync();
            if (semanticModel == null || root == null) return Error(id, "no semantic model");

            var node = root.FindToken(position).Parent;
            ISymbol? symbol = node == null
                ? null
                : semanticModel.GetSymbolInfo(node).Symbol ?? semanticModel.GetDeclaredSymbol(node);
            if (symbol == null) return Error(id, "no symbol at that position");

            var references = new JArray();
            foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbol, solution))
            {
                foreach (var location in referenced.Locations)
                {
                    if (references.Count >= 200) break;
                    var span = location.Location.GetLineSpan();
                    references.Add(new JObject
                    {
                        ["file"] = span.Path,
                        ["line"] = span.StartLinePosition.Line + 1,
                        ["column"] = span.StartLinePosition.Character + 1,
                    });
                }
            }

            return Ok(id, new JObject
            {
                ["symbol"] = symbol.ToDisplayString(),
                ["references"] = references,
            });
        }

        private string GetCallStack(string? id, DTE2 dte)
        {
            Debugger debugger = dte.Debugger;
            if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode) return Error(id, "not in break mode");

            var frames = new JArray();
            var thread = debugger.CurrentThread;
            if (thread != null)
            {
                foreach (StackFrame frame in thread.StackFrames)
                {
                    frames.Add(new JObject
                    {
                        ["function"] = frame.FunctionName,
                        ["language"] = frame.Language,
                    });
                }
            }

            return Ok(id, new JObject { ["frames"] = frames });
        }

        private string EvaluateExpression(string? id, DTE2 dte, string? expression)
        {
            if (string.IsNullOrEmpty(expression)) return Error(id, "expression is required");

            Debugger debugger = dte.Debugger;
            if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode) return Error(id, "not in break mode");

            Expression evaluated = debugger.GetExpression(expression, UseAutoExpandRules: true);
            return Ok(id, new JObject
            {
                ["name"] = evaluated.Name,
                ["value"] = evaluated.Value,
                ["type"] = evaluated.Type,
                ["isValid"] = evaluated.IsValidValue,
            });
        }

        private string ListBreakpoints(string? id, DTE2 dte)
        {
            var breakpoints = new JArray();
            foreach (Breakpoint breakpoint in dte.Debugger.Breakpoints)
            {
                breakpoints.Add(new JObject
                {
                    ["file"] = breakpoint.File,
                    ["line"] = breakpoint.FileLine,
                    ["enabled"] = breakpoint.Enabled,
                    ["condition"] = breakpoint.Condition,
                });
            }

            return Ok(id, new JObject { ["breakpoints"] = breakpoints });
        }

        private string RemoveBreakpoint(string? id, DTE2 dte, string? file, int? line)
        {
            if (string.IsNullOrEmpty(file) || !line.HasValue) return Error(id, "file and line are required");

            foreach (Breakpoint breakpoint in dte.Debugger.Breakpoints)
            {
                if (string.Equals(breakpoint.File, file, StringComparison.OrdinalIgnoreCase) &&
                    breakpoint.FileLine == line.Value)
                {
                    breakpoint.Delete();
                    return Ok(id, new JObject { ["removed"] = true });
                }
            }

            return Ok(id, new JObject { ["removed"] = false });
        }

        // --- navigation / editor state ---

        private string OpenFile(string? id, DTE2 dte, string? path, int? line)
        {
            if (string.IsNullOrEmpty(path)) return Error(id, "path is required");

            dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
            if (line.HasValue && dte.ActiveDocument?.Selection is TextSelection selection)
            {
                selection.GotoLine(line.Value, Select: false);
            }

            return Ok(id, new JObject { ["opened"] = path });
        }

        private string SaveDocument(string? id, DTE2 dte, string? path)
        {
            Document? doc = FindDocument(dte, path) ?? dte.ActiveDocument;
            if (doc == null) return Error(id, "no matching document");

            doc.Save();
            return Ok(id, new JObject { ["saved"] = doc.FullName });
        }

        private string CloseTab(string? id, DTE2 dte, string? path)
        {
            Document? doc = FindDocument(dte, path);
            if (doc == null) return Error(id, "no matching document");

            doc.Close(vsSaveChanges.vsSaveChangesPrompt);
            return Ok(id, new JObject { ["closed"] = path });
        }

        private string FormatDocument(string? id, DTE2 dte, string? path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
            }

            dte.ExecuteCommand("Edit.FormatDocument");
            return Ok(id, new JObject { ["formatted"] = true });
        }

        // --- single-writer mutation ---

        private async Task<string> OpenDiffAsync(string? id, string? file, string? proposedText)
        {
            if (string.IsNullOrEmpty(file)) return Error(id, "file is required");

            await _diff.ShowAsync(file!, proposedText ?? string.Empty, CancellationToken.None);
            return Ok(id, new JObject { ["shown"] = true });
        }

        // --- build ---

        private string BuildSolution(string? id, DTE2 dte)
        {
            SolutionBuild build = dte.Solution.SolutionBuild;
            build.Build(WaitForBuildToFinish: true);

            return Ok(id, new JObject
            {
                ["failedProjects"] = build.LastBuildInfo, // 0 == success
                ["succeeded"] = build.LastBuildInfo == 0,
            });
        }

        // --- tests / vcs (run fixed tools off the UI thread) ---

        private async Task<string> RunTestsAsync(string? id, DTE2 dte, string? filter)
        {
            string? dir = SolutionDirectory(dte);
            if (string.IsNullOrEmpty(dir)) return Error(id, "no solution directory");

            // Only a conservative filter is allowed through; reject anything that could break
            // out of the single --filter argument.
            string args = "test --nologo";
            if (!string.IsNullOrEmpty(filter))
            {
                if (filter!.IndexOfAny(new[] { '"', '\'', '&', '|', '<', '>', '\n', '\r' }) >= 0)
                {
                    return Error(id, "invalid filter");
                }
                args += $" --filter \"{filter}\"";
            }

            var (exitCode, output) = await Task.Run(() => RunProcess("dotnet", args, dir!));
            return Ok(id, new JObject
            {
                ["exitCode"] = exitCode,
                ["succeeded"] = exitCode == 0,
                ["output"] = Tail(output, 8000),
            });
        }

        private async Task<string> GitStatusAsync(string? id, DTE2 dte)
        {
            string? dir = SolutionDirectory(dte);
            if (string.IsNullOrEmpty(dir)) return Error(id, "no solution directory");

            var (exitCode, output) = await Task.Run(() => RunProcess("git", "status --porcelain=v1 --branch", dir!));
            if (exitCode != 0) return Error(id, "git is unavailable or this is not a repository");

            return Ok(id, new JObject { ["status"] = output });
        }

        private static string? SolutionDirectory(DTE2 dte)
        {
            try
            {
                string? sln = dte.Solution?.FullName;
                return string.IsNullOrEmpty(sln) ? null : Path.GetDirectoryName(sln);
            }
            catch
            {
                return null;
            }
        }

        private static (int exitCode, string output) RunProcess(string fileName, string arguments, string workingDirectory)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(fileName, arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using (var process = System.Diagnostics.Process.Start(psi))
            {
                if (process == null) return (-1, "failed to start process");

                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                var combined = new StringBuilder(stdout);
                if (stderr.Length > 0) combined.Append(stderr);
                return (process.ExitCode, combined.ToString());
            }
        }

        private static string Tail(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars) return value;
            return "...(truncated)...\n" + value.Substring(value.Length - maxChars);
        }

        // --- debugger ---

        private string AddBreakpoint(string? id, DTE2 dte, string? file, int? line)
        {
            if (string.IsNullOrEmpty(file) || !line.HasValue) return Error(id, "file and line are required");

            dte.Debugger.Breakpoints.Add(File: file, Line: line.Value);
            return Ok(id, new JObject { ["breakpoint"] = new JObject { ["file"] = file, ["line"] = line.Value } });
        }

        private string DebugControl(string? id, DTE2 dte, string? action)
        {
            Debugger debugger = dte.Debugger;
            switch (action)
            {
                case "continue": debugger.Go(WaitForBreakOrEnd: false); break;
                case "stepOver": debugger.StepOver(WaitForBreakOrEnd: false); break;
                case "stepInto": debugger.StepInto(WaitForBreakOrEnd: false); break;
                case "stepOut": debugger.StepOut(WaitForBreakOrEnd: false); break;
                case "break": debugger.Break(WaitForBreakMode: false); break;
                case "stop": debugger.Stop(WaitForDesignMode: false); break;
                default: return Error(id, "unknown debug action: " + (action ?? "<null>"));
            }

            return Ok(id, new JObject { ["action"] = action });
        }

        // --- helpers ---

        private static Document? FindDocument(DTE2 dte, string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            foreach (Document doc in dte.Documents)
            {
                if (string.Equals(doc.FullName, path, StringComparison.OrdinalIgnoreCase))
                {
                    return doc;
                }
            }
            return null;
        }

        private static string Ok(string? id, JObject result)
        {
            return new JObject { ["id"] = id, ["ok"] = true, ["result"] = result }
                .ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string Error(string? id, string message)
        {
            return new JObject { ["id"] = id, ["ok"] = false, ["error"] = message }
                .ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
