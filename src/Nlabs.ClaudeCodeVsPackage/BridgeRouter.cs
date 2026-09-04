using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Nlabs.ClaudeCodeVsPackage.Bridge;

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

                // --- navigation / editor state ---
                case "openFile": return OpenFile(id, dte, (string?)payload["path"], (int?)payload["line"]);
                case "saveDocument": return SaveDocument(id, dte, (string?)payload["path"]);
                case "closeTab": return CloseTab(id, dte, (string?)payload["path"]);
                case "formatDocument": return FormatDocument(id, dte, (string?)payload["path"]);

                // --- single-writer mutation ---
                case "openDiff": return await OpenDiffAsync(id, (string?)payload["file"], (string?)payload["proposedText"]);

                // --- build (beyond the built-in set) ---
                case "buildSolution": return BuildSolution(id, dte);

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
