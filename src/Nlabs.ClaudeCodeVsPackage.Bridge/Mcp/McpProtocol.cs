using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Mcp;

/// <summary>One tool exposed to Claude Code: its name, description and JSON input schema.</summary>
public sealed class McpToolDefinition
{
    public McpToolDefinition(string name, string description, JObject inputSchema)
    {
        Name = name;
        Description = description;
        InputSchema = inputSchema;
    }

    public string Name { get; }
    public string Description { get; }
    public JObject InputSchema { get; }
}

/// <summary>Thrown by a catalog when asked to call a tool it does not know.</summary>
public sealed class McpUnknownToolException : Exception
{
    public McpUnknownToolException(string name) : base("Unknown tool: " + name) { }
}

/// <summary>The set of tools and how to invoke them. The Visual Studio layer implements this.</summary>
public interface IMcpToolCatalog
{
    IReadOnlyList<McpToolDefinition> Tools { get; }

    /// <summary>Runs a tool and returns its result object; throws <see cref="McpUnknownToolException"/> if unknown.</summary>
    Task<JObject> CallAsync(string name, JObject arguments, CancellationToken cancellationToken);
}

/// <summary>
/// The Model Context Protocol handler, hand-written for net472 (the VS shell runtime,
/// where the modern MCP SDK cannot run). It speaks the JSON-RPC methods Claude Code uses
/// over the IDE WebSocket - initialize, tools/list, tools/call, ping - so the extension
/// is a first-class /ide connection rather than an added mcp server.
///
/// It is deliberately free of any Visual Studio dependency: the tools come from an
/// injected catalog, so the whole protocol is unit-tested without opening VS.
/// </summary>
public sealed class McpProtocol
{
    // Claude Code's IDE server advertises this version; we echo the client's if it sends one.
    private const string DefaultProtocolVersion = "2024-11-05";

    private readonly IMcpToolCatalog _catalog;
    private readonly string _serverName;
    private readonly string _serverVersion;

    public McpProtocol(IMcpToolCatalog catalog, string serverName, string serverVersion)
    {
        _catalog = catalog;
        _serverName = serverName;
        _serverVersion = serverVersion;
    }

    /// <summary>
    /// Handles one JSON-RPC message and returns the response JSON, or null when the
    /// message is a notification that takes no reply.
    /// </summary>
    public async Task<string?> HandleAsync(string requestJson, CancellationToken cancellationToken)
    {
        JObject request;
        try
        {
            request = JObject.Parse(requestJson);
        }
        catch
        {
            return Error(null, -32700, "Parse error");
        }

        JToken? id = request["id"];
        var method = (string?)request["method"];
        if (method == null)
        {
            return Error(id, -32600, "Invalid Request");
        }

        switch (method)
        {
            case "initialize":
                return Result(id, Initialize(request));

            case "ping":
                return Result(id, new JObject());

            case "tools/list":
                return Result(id, ToolsList());

            case "tools/call":
                return await ToolsCallAsync(id, request, cancellationToken).ConfigureAwait(false);

            default:
                // Any notification (no reply expected) is acknowledged by staying silent.
                if (method.StartsWith("notifications/", StringComparison.Ordinal))
                {
                    return null;
                }
                return Error(id, -32601, "Method not found: " + method);
        }
    }

    private JObject Initialize(JObject request)
    {
        var requested = (string?)request["params"]?["protocolVersion"];
        return new JObject
        {
            ["protocolVersion"] = string.IsNullOrEmpty(requested) ? DefaultProtocolVersion : requested,
            ["capabilities"] = new JObject
            {
                ["logging"] = new JObject(),
                ["prompts"] = new JObject { ["listChanged"] = true },
                ["tools"] = new JObject { ["listChanged"] = true },
            },
            ["serverInfo"] = new JObject { ["name"] = _serverName, ["version"] = _serverVersion },
        };
    }

    /// <summary>
    /// The notification that says the tool list is worth reading again.
    ///
    /// <c>initialize</c> advertises <c>tools.listChanged</c>, and a capability nobody ever
    /// exercises is a promise, not a feature: the client lists the tools once at connect and keeps
    /// that copy forever. A description that carries the solution's state - "there are compile
    /// errors right now" - is only worth composing if the client is told to come back for it.
    /// </summary>
    public static string ToolsListChanged()
    {
        return new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/tools/list_changed",
        }.ToString(Newtonsoft.Json.Formatting.None);
    }

    private JObject ToolsList()
    {
        var tools = new JArray();
        foreach (var tool in _catalog.Tools)
        {
            tools.Add(new JObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema,
            });
        }
        return new JObject { ["tools"] = tools };
    }

    private async Task<string?> ToolsCallAsync(JToken? id, JObject request, CancellationToken cancellationToken)
    {
        var parameters = request["params"] as JObject;
        var name = (string?)parameters?["name"];
        if (string.IsNullOrEmpty(name))
        {
            return Error(id, -32602, "Invalid params: missing tool name");
        }

        var arguments = parameters?["arguments"] as JObject ?? new JObject();

        try
        {
            JObject result = await _catalog.CallAsync(name!, arguments, cancellationToken).ConfigureAwait(false);
            return Result(id, AsToolResult(result));
        }
        catch (McpUnknownToolException)
        {
            return Error(id, -32601, "Unknown tool: " + name);
        }
        catch (Exception ex)
        {
            // A tool failure is reported as a tool result with isError - NOT a JSON-RPC
            // error - so a single failing call never drops the whole connection.
            return Result(id, ToolContent(ex.Message, isError: true));
        }
    }

    /// <summary>
    /// Turns what a catalog returned into an MCP tool result. A catalog usually returns
    /// its structured data, which we wrap as a single JSON text block - exactly how a
    /// native IDE serves its data tools. A catalog may instead hand back a ready result
    /// (an object with a "content" array), so a tool whose reply is a bare string token
    /// - close_tab's "TAB_CLOSED", a diff's "FILE_SAVED" - can shape its own content.
    /// </summary>
    private static JObject AsToolResult(JObject fromCatalog)
    {
        if (fromCatalog["content"] is JArray)
        {
            if (fromCatalog["isError"] == null) { fromCatalog["isError"] = false; }
            return fromCatalog;
        }
        return ToolContent(fromCatalog.ToString(Formatting.None), isError: false);
    }

    private static JObject ToolContent(string text, bool isError)
    {
        return new JObject
        {
            ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = text } },
            ["isError"] = isError,
        };
    }

    private static string Result(JToken? id, JObject result)
    {
        return new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["result"] = result,
        }.ToString(Formatting.None);
    }

    private static string Error(JToken? id, int code, string message)
    {
        return new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["error"] = new JObject { ["code"] = code, ["message"] = message },
        }.ToString(Formatting.None);
    }
}
