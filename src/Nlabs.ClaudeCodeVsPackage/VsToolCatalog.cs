using System;
using System.Collections.Generic;
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
using Nlabs.ClaudeCodeVsPackage.Bridge.Mcp;

// EnvDTE and Microsoft.CodeAnalysis both define Document/Solution/Project. The editor-state
// handlers mean the EnvDTE ones; the Roslyn equivalents are only ever reached through 'var'.
using Document = EnvDTE.Document;
using Solution = EnvDTE.Solution;
using Project = EnvDTE.Project;

// Every DTE handler is entered only after SwitchToMainThreadAsync, so all DTE access is on the
// UI thread. The threading analyzer cannot follow that across the awaits, so its UI-thread
// warnings are suppressed for this file; the invariant holds at each handler's entry.
#pragma warning disable VSTHRD010

namespace Nlabs.ClaudeCodeVsPackage
{
    /// <summary>
    /// The tools Visual Studio exposes to Claude Code over the native /ide connection.
    ///
    /// Because the extension is discovered as a first-class IDE (not added with
    /// <c>claude mcp add</c>), every tool here appears to the model WITHOUT an <c>mcp__</c>
    /// prefix - <c>openFile</c>, <c>getDiagnostics</c>, <c>openDiff</c> - so the built-in IDE
    /// tools resolve to Visual Studio. The names and shapes of those built-ins are matched
    /// exactly. Beyond them sit Visual Studio's own edge - solution build, test runs, the
    /// debugger and Roslyn symbol search - capabilities the standard IDE bridge does not carry.
    ///
    /// Single-writer principle: the agent proposes, the human applies. The only write path is
    /// <c>openDiff</c>, which shows a diff and waits for the developer to accept or reject it;
    /// nothing here edits a buffer or a file behind the developer's back.
    ///
    /// Leakage: results carry only what a tool was asked for. No machine identity, environment,
    /// account or token is ever placed in a payload.
    /// </summary>
    internal sealed class VsToolCatalog : IMcpToolCatalog
    {
        private readonly IAsyncServiceProvider _services;
        private readonly JoinableTaskFactory _jtf;
        private readonly DiffSession _diff;

        public VsToolCatalog(IAsyncServiceProvider services, JoinableTaskFactory jtf, DiffSession diff)
        {
            _services = services;
            _jtf = jtf;
            _diff = diff;
            Tools = BuildTools();
        }

        public IReadOnlyList<McpToolDefinition> Tools { get; }

        public async Task<JObject> CallAsync(string name, JObject args, CancellationToken cancellationToken)
        {
            switch (name)
            {
                // --- native built-in IDE tools (exact names + shapes) ---
                case "openFile": return await OnUiAsync(dte => OpenFile(dte, args));
                case "openDiff": return await OpenDiffAsync(args, cancellationToken);
                case "getDiagnostics": return await OnUiAsync(dte => GetDiagnostics(dte, args));
                case "getOpenEditors": return await OnUiAsync(GetOpenEditors);
                case "getCurrentSelection": return await OnUiAsync(GetCurrentSelection);
                case "getLatestSelection": return await OnUiAsync(GetCurrentSelection);
                case "getWorkspaceFolders": return await OnUiAsync(GetWorkspaceFolders);
                case "checkDocumentDirty": return await OnUiAsync(dte => CheckDocumentDirty(dte, args));
                case "saveDocument": return await OnUiAsync(dte => SaveDocument(dte, args));
                case "close_tab": return await OnUiAsync(dte => CloseTab(dte, args));
                case "closeAllDiffTabs": return await OnUiAsync(CloseAllDiffTabs);

                // --- Visual Studio's edge (clear, un-prefixed names) ---
                case "buildSolution": return await OnUiAsync(BuildSolution);
                case "runTests": return await RunTestsAsync(args);
                case "getSolutionStructure": return await OnUiAsync(GetSolutionStructure);
                case "findSymbols": return await FindSymbolsAsync(args, cancellationToken);
                case "findReferences": return await FindReferencesAsync(args, cancellationToken);
                case "formatDocument": return await OnUiAsync(dte => FormatDocument(dte, args));
                case "getDebugState": return await OnUiAsync(GetDebugState);
                case "getCallStack": return await OnUiAsync(GetCallStack);
                case "evaluateExpression": return await OnUiAsync(dte => EvaluateExpression(dte, args));
                case "listBreakpoints": return await OnUiAsync(ListBreakpoints);
                case "addBreakpoint": return await OnUiAsync(dte => AddBreakpoint(dte, args));
                case "removeBreakpoint": return await OnUiAsync(dte => RemoveBreakpoint(dte, args));
                case "debugControl": return await OnUiAsync(dte => DebugControl(dte, args));
                case "gitStatus": return await GitStatusAsync();

                default: throw new McpUnknownToolException(name);
            }
        }

        // --- UI-thread plumbing ---

        private async Task<JObject> OnUiAsync(Func<DTE2, JObject> body)
        {
            await _jtf.SwitchToMainThreadAsync();
            var dte = await _services.GetServiceAsync(typeof(DTE)) as DTE2;
            if (dte == null) throw new InvalidOperationException("Visual Studio automation (DTE) is unavailable.");
            return body(dte);
        }

        // ============================ native built-in tools ============================

        private JObject OpenFile(DTE2 dte, JObject args)
        {
            string path = Require(args, "filePath");
            dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);

            if (dte.ActiveDocument?.Selection is TextSelection selection)
            {
                int? startLine = (int?)args["startLine"];
                int? endLine = (int?)args["endLine"];
                if (startLine.HasValue)
                {
                    selection.MoveToLineAndOffset(startLine.Value, 1, false);
                    if (endLine.HasValue)
                    {
                        selection.MoveToLineAndOffset(endLine.Value, 1, true);
                    }
                }
            }

            return new JObject { ["success"] = true, ["filePath"] = path };
        }

        private async Task<JObject> OpenDiffAsync(JObject args, CancellationToken ct)
        {
            // Native openDiff: propose new_file_contents for a file; wait for the human's verdict.
            string realPath = (string?)args["old_file_path"] ?? (string?)args["new_file_path"]
                ?? throw new ArgumentException("old_file_path is required.");
            string proposed = (string?)args["new_file_contents"] ?? string.Empty;
            string tabName = (string?)args["tab_name"] ?? Path.GetFileName(realPath);

            DiffOutcome outcome = await _diff.ShowAsync(realPath, proposed, tabName, ct).ConfigureAwait(false);

            // Deferred result, exactly as the CLI expects: two text blocks.
            return outcome.Accepted
                ? Blocks("FILE_SAVED", outcome.FinalContents)
                : Blocks("DIFF_REJECTED", outcome.TabName);
        }

        private JObject GetDiagnostics(DTE2 dte, JObject args)
        {
            string? filter = (string?)args["uri"];
            string? filterPath = ToLocalPath(filter);

            var byFile = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
            var items = dte.ToolWindows.ErrorList.ErrorItems;
            for (int i = 1; i <= items.Count; i++)
            {
                ErrorItem item = items.Item(i);
                string file = item.FileName ?? string.Empty;
                if (file.Length == 0) continue;
                if (filterPath != null && !string.Equals(file, filterPath, StringComparison.OrdinalIgnoreCase)) continue;

                if (!byFile.TryGetValue(file, out var list))
                {
                    list = new JArray();
                    byFile[file] = list;
                }

                int line = Math.Max(0, item.Line - 1);
                int character = Math.Max(0, item.Column - 1);
                list.Add(new JObject
                {
                    ["message"] = item.Description,
                    ["severity"] = Severity(item.ErrorLevel),
                    ["range"] = new JObject
                    {
                        ["start"] = new JObject { ["line"] = line, ["character"] = character },
                        ["end"] = new JObject { ["line"] = line, ["character"] = character },
                    },
                    ["source"] = item.Project,
                });
            }

            var result = new JArray();
            foreach (var pair in byFile)
            {
                result.Add(new JObject { ["uri"] = FileUri(pair.Key), ["diagnostics"] = pair.Value });
            }

            return Payload(result);
        }

        private JObject GetOpenEditors(DTE2 dte)
        {
            var editors = new JArray();
            Document? active = dte.ActiveDocument;
            foreach (Document doc in dte.Documents)
            {
                editors.Add(new JObject
                {
                    ["uri"] = FileUri(doc.FullName),
                    ["filePath"] = doc.FullName,
                    ["fileName"] = doc.Name,
                    ["label"] = doc.Name,
                    ["languageId"] = LanguageIdOf(doc.FullName),
                    ["isActive"] = active != null && string.Equals(active.FullName, doc.FullName, StringComparison.OrdinalIgnoreCase),
                    ["isDirty"] = !doc.Saved,
                    ["isUntitled"] = false,
                });
            }

            return Payload(editors);
        }

        private JObject GetCurrentSelection(DTE2 dte)
        {
            Document? doc = dte.ActiveDocument;
            if (doc == null || !(doc.Selection is TextSelection selection))
            {
                return new JObject { ["success"] = false };
            }

            var start = new JObject
            {
                ["line"] = Math.Max(0, selection.TopPoint.Line - 1),
                ["character"] = Math.Max(0, selection.TopPoint.LineCharOffset - 1),
            };
            var end = new JObject
            {
                ["line"] = Math.Max(0, selection.BottomPoint.Line - 1),
                ["character"] = Math.Max(0, selection.BottomPoint.LineCharOffset - 1),
            };

            return new JObject
            {
                ["success"] = true,
                ["text"] = selection.Text,
                ["filePath"] = doc.FullName,
                ["fileUrl"] = FileUri(doc.FullName),
                ["selection"] = new JObject
                {
                    ["start"] = start,
                    ["end"] = end,
                    ["isEmpty"] = selection.IsEmpty,
                },
            };
        }

        private JObject GetWorkspaceFolders(DTE2 dte)
        {
            var folders = new JArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Solution? solution = dte.Solution;

            if (solution != null)
            {
                foreach (Project project in solution.Projects)
                {
                    string? dir = null;
                    try { if (!string.IsNullOrEmpty(project.FullName)) dir = Path.GetDirectoryName(project.FullName); }
                    catch { /* solution folders have no FullName */ }

                    if (dir == null || !seen.Add(dir)) continue;
                    folders.Add(new JObject
                    {
                        ["name"] = Path.GetFileName(dir),
                        ["uri"] = FileUri(dir),
                        ["path"] = dir,
                    });
                }
            }

            string? root = SolutionDirectory(dte);
            return new JObject
            {
                ["folders"] = folders,
                ["rootPath"] = root,
            };
        }

        private JObject CheckDocumentDirty(DTE2 dte, JObject args)
        {
            string path = Require(args, "filePath");
            Document? doc = FindDocument(dte, path);
            if (doc == null)
            {
                return new JObject { ["success"] = false, ["filePath"] = path };
            }

            return new JObject
            {
                ["success"] = true,
                ["filePath"] = doc.FullName,
                ["isDirty"] = !doc.Saved,
                ["isUntitled"] = false,
            };
        }

        private JObject SaveDocument(DTE2 dte, JObject args)
        {
            string path = Require(args, "filePath");
            Document? doc = FindDocument(dte, path);
            if (doc == null) return new JObject { ["success"] = false, ["filePath"] = path };

            doc.Save();
            return new JObject { ["success"] = true, ["filePath"] = doc.FullName };
        }

        private JObject CloseTab(DTE2 dte, JObject args)
        {
            string tabName = Require(args, "tab_name");
            foreach (Document doc in dte.Documents)
            {
                if (string.Equals(doc.Name, tabName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(doc.FullName, tabName, StringComparison.OrdinalIgnoreCase))
                {
                    doc.Close(vsSaveChanges.vsSaveChangesPrompt);
                    return Blocks("TAB_CLOSED");
                }
            }
            return Blocks("TAB_CLOSED");
        }

        private JObject CloseAllDiffTabs(DTE2 dte)
        {
            int closed = 0;
            // Comparison windows are tool/document windows whose caption we set when opening a diff.
            foreach (Window window in dte.Windows.Cast<Window>().ToArray())
            {
                try
                {
                    if (window.Kind == "Document" && window.Caption != null &&
                        (window.Caption.StartsWith("Proposed", StringComparison.Ordinal) ||
                         window.Caption.IndexOf("Diff", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        window.Close();
                        closed++;
                    }
                }
                catch { /* some windows refuse Close; skip */ }
            }
            return Blocks("CLOSED_" + closed + "_DIFF_TABS");
        }

        // ============================ Visual Studio's edge ============================

        private JObject BuildSolution(DTE2 dte)
        {
            SolutionBuild build = dte.Solution.SolutionBuild;
            build.Build(WaitForBuildToFinish: true);
            return new JObject
            {
                ["failedProjects"] = build.LastBuildInfo, // 0 == success
                ["succeeded"] = build.LastBuildInfo == 0,
            };
        }

        private async Task<JObject> RunTestsAsync(JObject args)
        {
            string? dir = await UiSolutionDirectoryAsync();
            if (string.IsNullOrEmpty(dir)) throw new InvalidOperationException("No solution directory.");

            string testArgs = "test --nologo";
            string? filter = (string?)args["filter"];
            if (!string.IsNullOrEmpty(filter))
            {
                // Reject anything that could break out of the single quoted --filter argument.
                if (filter!.IndexOfAny(new[] { '"', '\'', '&', '|', '<', '>', '\n', '\r' }) >= 0)
                {
                    throw new ArgumentException("Invalid test filter.");
                }
                testArgs += $" --filter \"{filter}\"";
            }

            var (exitCode, output) = await Task.Run(() => RunProcess("dotnet", testArgs, dir!)).ConfigureAwait(false);
            return new JObject
            {
                ["exitCode"] = exitCode,
                ["succeeded"] = exitCode == 0,
                ["output"] = Tail(output, 8000),
            };
        }

        private JObject GetSolutionStructure(DTE2 dte)
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

            return new JObject { ["projects"] = projects };
        }

        private async Task<JObject> FindSymbolsAsync(JObject args, CancellationToken ct)
        {
            string query = Require(args, "query");

            await _jtf.SwitchToMainThreadAsync();
            var solution = await RoslynSolutionAsync();
            if (solution == null) throw new InvalidOperationException("No Roslyn workspace.");

            var found = await SymbolFinder.FindSourceDeclarationsAsync(solution, query, ignoreCase: true, ct).ConfigureAwait(false);

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

            return new JObject { ["symbols"] = symbols };
        }

        private async Task<JObject> FindReferencesAsync(JObject args, CancellationToken ct)
        {
            string file = Require(args, "file");
            int line = RequireInt(args, "line");
            int column = RequireInt(args, "column");

            await _jtf.SwitchToMainThreadAsync();
            var solution = await RoslynSolutionAsync();
            if (solution == null) throw new InvalidOperationException("No Roslyn workspace.");

            var documentId = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault();
            var document = documentId != null ? solution.GetDocument(documentId) : null;
            if (document == null) throw new InvalidOperationException("File is not part of the solution.");

            var text = await document.GetTextAsync(ct).ConfigureAwait(false);
            if (line < 1 || line > text.Lines.Count) throw new ArgumentException("Line out of range.");
            int position = text.Lines[line - 1].Start + Math.Max(0, column - 1);

            var semanticModel = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
            var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            if (semanticModel == null || root == null) throw new InvalidOperationException("No semantic model.");

            var node = root.FindToken(position).Parent;
            ISymbol? symbol = node == null
                ? null
                : semanticModel.GetSymbolInfo(node).Symbol ?? semanticModel.GetDeclaredSymbol(node);
            if (symbol == null) throw new InvalidOperationException("No symbol at that position.");

            var references = new JArray();
            foreach (var referenced in await SymbolFinder.FindReferencesAsync(symbol, solution, ct).ConfigureAwait(false))
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

            return new JObject { ["symbol"] = symbol.ToDisplayString(), ["references"] = references };
        }

        private JObject FormatDocument(DTE2 dte, JObject args)
        {
            string? path = (string?)args["filePath"];
            if (!string.IsNullOrEmpty(path))
            {
                dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
            }
            dte.ExecuteCommand("Edit.FormatDocument");
            return new JObject { ["success"] = true };
        }

        private JObject GetDebugState(DTE2 dte)
        {
            Debugger debugger = dte.Debugger;
            bool inBreak = debugger != null && debugger.CurrentMode == dbgDebugMode.dbgBreakMode;
            var result = new JObject { ["inBreakMode"] = inBreak };
            if (inBreak && debugger!.CurrentStackFrame != null)
            {
                result["function"] = debugger.CurrentStackFrame.FunctionName;
            }
            return result;
        }

        private JObject GetCallStack(DTE2 dte)
        {
            Debugger debugger = dte.Debugger;
            if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode) throw new InvalidOperationException("Not in break mode.");

            var frames = new JArray();
            var thread = debugger.CurrentThread;
            if (thread != null)
            {
                foreach (EnvDTE.StackFrame frame in thread.StackFrames)
                {
                    frames.Add(new JObject { ["function"] = frame.FunctionName, ["language"] = frame.Language });
                }
            }
            return new JObject { ["frames"] = frames };
        }

        private JObject EvaluateExpression(DTE2 dte, JObject args)
        {
            string expression = Require(args, "expression");
            Debugger debugger = dte.Debugger;
            if (debugger.CurrentMode != dbgDebugMode.dbgBreakMode) throw new InvalidOperationException("Not in break mode.");

            Expression evaluated = debugger.GetExpression(expression, UseAutoExpandRules: true);
            return new JObject
            {
                ["name"] = evaluated.Name,
                ["value"] = evaluated.Value,
                ["type"] = evaluated.Type,
                ["isValid"] = evaluated.IsValidValue,
            };
        }

        private JObject ListBreakpoints(DTE2 dte)
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
            return new JObject { ["breakpoints"] = breakpoints };
        }

        private JObject AddBreakpoint(DTE2 dte, JObject args)
        {
            string file = Require(args, "file");
            int line = RequireInt(args, "line");
            dte.Debugger.Breakpoints.Add(File: file, Line: line);
            return new JObject { ["breakpoint"] = new JObject { ["file"] = file, ["line"] = line } };
        }

        private JObject RemoveBreakpoint(DTE2 dte, JObject args)
        {
            string file = Require(args, "file");
            int line = RequireInt(args, "line");
            foreach (Breakpoint breakpoint in dte.Debugger.Breakpoints)
            {
                if (string.Equals(breakpoint.File, file, StringComparison.OrdinalIgnoreCase) &&
                    breakpoint.FileLine == line)
                {
                    breakpoint.Delete();
                    return new JObject { ["removed"] = true };
                }
            }
            return new JObject { ["removed"] = false };
        }

        private JObject DebugControl(DTE2 dte, JObject args)
        {
            string action = Require(args, "action");
            Debugger debugger = dte.Debugger;
            switch (action)
            {
                case "continue": debugger.Go(WaitForBreakOrEnd: false); break;
                case "stepOver": debugger.StepOver(WaitForBreakOrEnd: false); break;
                case "stepInto": debugger.StepInto(WaitForBreakOrEnd: false); break;
                case "stepOut": debugger.StepOut(WaitForBreakOrEnd: false); break;
                case "break": debugger.Break(WaitForBreakMode: false); break;
                case "stop": debugger.Stop(WaitForDesignMode: false); break;
                default: throw new ArgumentException("Unknown debug action: " + action);
            }
            return new JObject { ["action"] = action };
        }

        private async Task<JObject> GitStatusAsync()
        {
            string? dir = await UiSolutionDirectoryAsync();
            if (string.IsNullOrEmpty(dir)) throw new InvalidOperationException("No solution directory.");

            var (exitCode, output) = await Task.Run(() => RunProcess("git", "status --porcelain=v1 --branch", dir!)).ConfigureAwait(false);
            if (exitCode != 0) throw new InvalidOperationException("Git is unavailable or this is not a repository.");
            return new JObject { ["status"] = output };
        }

        // ============================ helpers ============================

        private async Task<Microsoft.CodeAnalysis.Solution?> RoslynSolutionAsync()
        {
            var componentModel = await _services.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            var workspace = componentModel?.GetService<VisualStudioWorkspace>();
            return workspace?.CurrentSolution;
        }

        private async Task<string?> UiSolutionDirectoryAsync()
        {
            await _jtf.SwitchToMainThreadAsync();
            var dte = await _services.GetServiceAsync(typeof(DTE)) as DTE2;
            return dte == null ? null : SolutionDirectory(dte);
        }

        private static string? SolutionDirectory(DTE2 dte)
        {
            try
            {
                string? sln = dte.Solution?.FullName;
                return string.IsNullOrEmpty(sln) ? null : Path.GetDirectoryName(sln);
            }
            catch { return null; }
        }

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
                        if (!string.IsNullOrEmpty(file) && File.Exists(file)) sink.Add(file);
                    }
                }
                catch { /* some items expose no file */ }
                CollectFiles(item.ProjectItems, sink, depth + 1);
            }
        }

        private static Document? FindDocument(DTE2 dte, string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            foreach (Document doc in dte.Documents)
            {
                if (string.Equals(doc.FullName, path, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(doc.Name, path, StringComparison.OrdinalIgnoreCase))
                {
                    return doc;
                }
            }
            return null;
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

        private static string Severity(vsBuildErrorLevel level)
        {
            switch (level)
            {
                case vsBuildErrorLevel.vsBuildErrorLevelHigh: return "Error";
                case vsBuildErrorLevel.vsBuildErrorLevelMedium: return "Warning";
                default: return "Info";
            }
        }

        private static string LanguageIdOf(string? path)
        {
            string ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            switch (ext)
            {
                case ".cs": return "csharp";
                case ".vb": return "vb";
                case ".ts": return "typescript";
                case ".js": return "javascript";
                case ".json": return "json";
                case ".xml": case ".csproj": case ".vbproj": return "xml";
                case ".xaml": return "xaml";
                case ".html": case ".cshtml": return "html";
                case ".css": return "css";
                case ".sql": return "sql";
                case ".md": return "markdown";
                default: return ext.TrimStart('.');
            }
        }

        private static string FileUri(string? path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try { return new Uri(path).AbsoluteUri; }
            catch { return path!; }
        }

        // Accepts either a file path or a file:// URI and returns a local path, or null.
        private static string? ToLocalPath(string? uriOrPath)
        {
            if (string.IsNullOrEmpty(uriOrPath)) return null;
            if (uriOrPath!.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                try { return new Uri(uriOrPath).LocalPath; } catch { return uriOrPath; }
            }
            return uriOrPath;
        }

        private static string Require(JObject args, string name)
        {
            var value = (string?)args[name];
            if (string.IsNullOrEmpty(value)) throw new ArgumentException($"'{name}' is required.");
            return value!;
        }

        private static int RequireInt(JObject args, string name)
        {
            var token = args[name];
            if (token == null || token.Type == JTokenType.Null) throw new ArgumentException($"'{name}' is required.");
            return (int)token;
        }

        // A tool result whose payload is a raw JSON value (e.g. an array); serialized as one text block.
        private static JObject Payload(JToken payload)
        {
            return new JObject
            {
                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = payload.ToString(Newtonsoft.Json.Formatting.None) } },
                ["isError"] = false,
            };
        }

        // A tool result made of one or more bare-string text blocks (TAB_CLOSED, FILE_SAVED, ...).
        private static JObject Blocks(params string[] texts)
        {
            var content = new JArray();
            foreach (var text in texts)
            {
                content.Add(new JObject { ["type"] = "text", ["text"] = text ?? string.Empty });
            }
            return new JObject { ["content"] = content, ["isError"] = false };
        }

        // ============================ tool catalog (names + schemas) ============================

        private static IReadOnlyList<McpToolDefinition> BuildTools()
        {
            return new List<McpToolDefinition>
            {
                // native built-ins
                Tool("openFile", "Open a file in the editor, optionally selecting a line range.",
                    Schema(new JObject
                    {
                        ["filePath"] = P("string", "Absolute path of the file to open."),
                        ["startLine"] = P("number", "1-based line to move the caret to / start a selection."),
                        ["endLine"] = P("number", "1-based line to extend the selection to."),
                        ["makeFrontmost"] = P("boolean", "Bring the editor to the foreground (default true)."),
                    }, "filePath")),

                Tool("openDiff", "Show a proposed change as a diff and wait for the developer to accept (save) or reject (close) it.",
                    Schema(new JObject
                    {
                        ["old_file_path"] = P("string", "Absolute path of the file being changed."),
                        ["new_file_path"] = P("string", "Absolute path of the proposed file (usually the same)."),
                        ["new_file_contents"] = P("string", "The full proposed contents."),
                        ["tab_name"] = P("string", "A label for the diff tab."),
                    }, "old_file_path", "new_file_contents", "tab_name")),

                Tool("getDiagnostics", "Return compiler/analyzer diagnostics, optionally for a single file.",
                    Schema(new JObject { ["uri"] = P("string", "Optional file path or file:// URI to filter to.") })),

                Tool("getOpenEditors", "List the currently open editor tabs.", Schema(new JObject())),
                Tool("getCurrentSelection", "Return the active editor's current selection.", Schema(new JObject())),
                Tool("getLatestSelection", "Return the most recent editor selection.", Schema(new JObject())),
                Tool("getWorkspaceFolders", "List the solution's project folders and root path.", Schema(new JObject())),

                Tool("checkDocumentDirty", "Report whether an open document has unsaved changes.",
                    Schema(new JObject { ["filePath"] = P("string", "Absolute path of the document.") }, "filePath")),
                Tool("saveDocument", "Save an open document.",
                    Schema(new JObject { ["filePath"] = P("string", "Absolute path of the document to save.") }, "filePath")),
                Tool("close_tab", "Close an open editor tab by name.",
                    Schema(new JObject { ["tab_name"] = P("string", "The tab (document) name to close.") }, "tab_name")),
                Tool("closeAllDiffTabs", "Close all open diff/comparison tabs.", Schema(new JObject())),

                // Visual Studio's edge
                Tool("buildSolution", "Build the open solution and report whether it succeeded.", Schema(new JObject())),
                Tool("runTests", "Run the solution's tests with `dotnet test`, optionally filtered.",
                    Schema(new JObject { ["filter"] = P("string", "Optional dotnet test --filter expression.") })),
                Tool("getSolutionStructure", "List the solution's projects and their files.", Schema(new JObject())),
                Tool("findSymbols", "Find source symbols across the solution by name (Roslyn).",
                    Schema(new JObject { ["query"] = P("string", "Symbol name to search for.") }, "query")),
                Tool("findReferences", "Find all references to the symbol at a file position (Roslyn).",
                    Schema(new JObject
                    {
                        ["file"] = P("string", "Absolute path of the file."),
                        ["line"] = P("number", "1-based line of the symbol."),
                        ["column"] = P("number", "1-based column of the symbol."),
                    }, "file", "line", "column")),
                Tool("formatDocument", "Format a document with Visual Studio's formatter.",
                    Schema(new JObject { ["filePath"] = P("string", "Optional path; defaults to the active document.") })),
                Tool("getDebugState", "Report whether the debugger is in break mode and where.", Schema(new JObject())),
                Tool("getCallStack", "Return the current call stack (break mode only).", Schema(new JObject())),
                Tool("evaluateExpression", "Evaluate an expression in the current debug context.",
                    Schema(new JObject { ["expression"] = P("string", "The expression to evaluate.") }, "expression")),
                Tool("listBreakpoints", "List all breakpoints.", Schema(new JObject())),
                Tool("addBreakpoint", "Add a line breakpoint.",
                    Schema(new JObject
                    {
                        ["file"] = P("string", "Absolute path of the file."),
                        ["line"] = P("number", "1-based line for the breakpoint."),
                    }, "file", "line")),
                Tool("removeBreakpoint", "Remove a line breakpoint.",
                    Schema(new JObject
                    {
                        ["file"] = P("string", "Absolute path of the file."),
                        ["line"] = P("number", "1-based line of the breakpoint."),
                    }, "file", "line")),
                Tool("debugControl", "Control the debugger: continue, stepOver, stepInto, stepOut, break, stop.",
                    Schema(new JObject { ["action"] = P("string", "One of continue|stepOver|stepInto|stepOut|break|stop.") }, "action")),
                Tool("gitStatus", "Return `git status` for the solution's repository.", Schema(new JObject())),
            };
        }

        private static McpToolDefinition Tool(string name, string description, JObject schema)
            => new McpToolDefinition(name, description, schema);

        private static JObject P(string type, string description)
            => new JObject { ["type"] = type, ["description"] = description };

        private static JObject Schema(JObject properties, params string[] required)
        {
            var schema = new JObject { ["type"] = "object", ["properties"] = properties };
            if (required.Length > 0) schema["required"] = new JArray(required);
            return schema;
        }
    }
}
