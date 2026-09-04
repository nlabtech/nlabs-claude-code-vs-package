using System;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Nlabs.ClaudeCodeVsPackage.Bridge;

namespace Nlabs.ClaudeCodeVsPackage
{
    /// <summary>
    /// Turns raw bridge messages into Visual Studio actions and sends replies back.
    ///
    /// This is the piece that makes the separate parts one working extension: it owns the
    /// server's MessageReceived event, parses the JSON envelope { id, type, payload },
    /// dispatches to a handler on the UI thread, and answers with { id, ok, result|error }.
    /// The agent (Claude Code) speaks this small protocol over the local WebSocket.
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
            // MessageReceived fires on a background thread; hop through the JTF so handlers
            // can touch VS on the UI thread. Exceptions are turned into an error reply.
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

            switch (type)
            {
                case "ping":
                    return Ok(id, new JObject { ["pong"] = true });
                case "get_active_document":
                    return await GetActiveDocumentAsync(id);
                case "get_diagnostics":
                    return await GetDiagnosticsAsync(id);
                case "get_debug_state":
                    return await GetDebugStateAsync(id);
                case "propose_diff":
                    return await ProposeDiffAsync(id, payload);
                default:
                    return Error(id, "unknown message type: " + (type ?? "<null>"));
            }
        }

        private async Task<string> GetActiveDocumentAsync(string? id)
        {
            await _jtf.SwitchToMainThreadAsync();

            var dte = await _services.GetServiceAsync(typeof(DTE)) as DTE;
            var doc = dte?.ActiveDocument;
            if (doc == null)
            {
                return Error(id, "no active document");
            }

            var result = new JObject { ["path"] = doc.FullName };
            if (doc.Selection is TextSelection selection)
            {
                result["selectedText"] = selection.Text;
                result["line"] = selection.CurrentLine;
            }

            return Ok(id, result);
        }

        private async Task<string> GetDiagnosticsAsync(string? id)
        {
            await _jtf.SwitchToMainThreadAsync();

            var dte = await _services.GetServiceAsync(typeof(DTE)) as DTE2;
            var items = dte?.ToolWindows.ErrorList.ErrorItems;

            var diagnostics = new JArray();
            if (items != null)
            {
                for (int i = 1; i <= items.Count; i++)
                {
                    ErrorItem item = items.Item(i);
                    diagnostics.Add(new JObject
                    {
                        ["file"] = item.FileName,
                        ["line"] = item.Line,
                        ["description"] = item.Description,
                        ["level"] = (int)item.ErrorLevel,
                    });
                }
            }

            return Ok(id, new JObject { ["diagnostics"] = diagnostics });
        }

        private async Task<string> GetDebugStateAsync(string? id)
        {
            await _jtf.SwitchToMainThreadAsync();

            var dte = await _services.GetServiceAsync(typeof(DTE)) as DTE;
            Debugger? debugger = dte?.Debugger;

            bool inBreak = debugger != null && debugger.CurrentMode == dbgDebugMode.dbgBreakMode;
            var result = new JObject { ["inBreakMode"] = inBreak };

            // The execution context comes from the debugger's stack frame - never the caret.
            if (inBreak && debugger!.CurrentStackFrame != null)
            {
                result["function"] = debugger.CurrentStackFrame.FunctionName;
            }

            return Ok(id, result);
        }

        private async Task<string> ProposeDiffAsync(string? id, JObject payload)
        {
            string? file = (string?)payload["file"];
            string? proposedText = (string?)payload["proposedText"];

            if (string.IsNullOrEmpty(file))
            {
                return Error(id, "file is required");
            }

            await _diff.ShowAsync(file!, proposedText ?? string.Empty, CancellationToken.None);
            return Ok(id, new JObject { ["shown"] = true });
        }

        private static string Ok(string? id, JObject result)
        {
            return new JObject
            {
                ["id"] = id,
                ["ok"] = true,
                ["result"] = result,
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string Error(string? id, string message)
        {
            return new JObject
            {
                ["id"] = id,
                ["ok"] = false,
                ["error"] = message,
            }.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
