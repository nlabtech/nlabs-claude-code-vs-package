using Newtonsoft.Json.Linq;
using Nlabs.ClaudeCodeVsPackage.Bridge.Mcp;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

public class McpProtocolTests
{
    private sealed class FakeCatalog : IMcpToolCatalog
    {
        private readonly bool _throwOnCall;

        public FakeCatalog(bool throwOnCall = false)
        {
            _throwOnCall = throwOnCall;
            Tools = new List<McpToolDefinition>
            {
                new McpToolDefinition(
                    "echo",
                    "Echoes the arguments back.",
                    new JObject { ["type"] = "object" }),
            };
        }

        public IReadOnlyList<McpToolDefinition> Tools { get; }

        public Task<JObject> CallAsync(string name, JObject arguments, CancellationToken cancellationToken)
        {
            if (_throwOnCall) throw new InvalidOperationException("boom");
            if (name == "echo") return Task.FromResult(new JObject { ["echoed"] = arguments });

            // A tool that returns a ready MCP result (bare-string content blocks), like the
            // native openDiff's FILE_SAVED reply; the protocol must pass it through untouched.
            if (name == "twoBlocks")
            {
                return Task.FromResult(new JObject
                {
                    ["content"] = new JArray
                    {
                        new JObject { ["type"] = "text", ["text"] = "FILE_SAVED" },
                        new JObject { ["type"] = "text", ["text"] = "final body" },
                    },
                });
            }

            throw new McpUnknownToolException(name);
        }
    }

    private static McpProtocol NewProtocol(bool throwOnCall = false)
        => new McpProtocol(new FakeCatalog(throwOnCall), "Visual Studio", "0.1.0");

    private static async Task<JObject> Handle(McpProtocol protocol, string request)
        => JObject.Parse(await protocol.HandleAsync(request, CancellationToken.None) ?? "{}");

    [Fact]
    public async Task Initialize_returns_server_info_and_echoes_protocol_version()
    {
        var response = await Handle(NewProtocol(),
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\"}}");

        Assert.Equal("Visual Studio", (string?)response["result"]!["serverInfo"]!["name"]);
        Assert.Equal("2024-11-05", (string?)response["result"]!["protocolVersion"]);
        Assert.NotNull(response["result"]!["capabilities"]!["tools"]);
    }

    [Fact]
    public async Task Tools_list_returns_the_catalog()
    {
        var response = await Handle(NewProtocol(),
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");

        var tools = (JArray)response["result"]!["tools"]!;
        Assert.Single(tools);
        Assert.Equal("echo", (string?)tools[0]!["name"]);
        Assert.NotNull(tools[0]!["inputSchema"]);
    }

    [Fact]
    public async Task Tools_call_dispatches_and_wraps_the_result()
    {
        var response = await Handle(NewProtocol(),
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{\"text\":\"hi\"}}}");

        Assert.False((bool)response["result"]!["isError"]!);
        string text = (string)((JArray)response["result"]!["content"]!)[0]!["text"]!;
        Assert.Contains("echoed", text);
        Assert.Contains("hi", text);
    }

    [Fact]
    public async Task A_ready_content_result_is_passed_through_unchanged()
    {
        var response = await Handle(NewProtocol(),
            "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/call\",\"params\":{\"name\":\"twoBlocks\",\"arguments\":{}}}");

        var content = (JArray)response["result"]!["content"]!;
        Assert.Equal(2, content.Count);
        Assert.Equal("FILE_SAVED", (string)content[0]!["text"]!);
        Assert.Equal("final body", (string)content[1]!["text"]!);
        Assert.False((bool)response["result"]!["isError"]!);
    }

    [Fact]
    public async Task A_failing_tool_is_a_result_with_isError_not_a_connection_error()
    {
        var response = await Handle(NewProtocol(throwOnCall: true),
            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"echo\",\"arguments\":{}}}");

        Assert.Null(response["error"]);
        Assert.True((bool)response["result"]!["isError"]!);
        Assert.Contains("boom", (string)((JArray)response["result"]!["content"]!)[0]!["text"]!);
    }

    [Fact]
    public async Task Unknown_tool_is_a_method_not_found_error()
    {
        var response = await Handle(NewProtocol(),
            "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"nope\"}}");

        Assert.Equal(-32601, (int)response["error"]!["code"]!);
    }

    [Fact]
    public async Task Invalid_json_is_a_parse_error()
    {
        var response = await Handle(NewProtocol(), "{ this is not json");

        Assert.Equal(-32700, (int)response["error"]!["code"]!);
    }

    [Fact]
    public async Task Unknown_method_is_method_not_found()
    {
        var response = await Handle(NewProtocol(),
            "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"does/notExist\"}");

        Assert.Equal(-32601, (int)response["error"]!["code"]!);
    }

    [Fact]
    public async Task An_initialized_notification_gets_no_response()
    {
        string? response = await NewProtocol().HandleAsync(
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", CancellationToken.None);

        Assert.Null(response);
    }

    [Fact]
    public async Task Ping_returns_an_empty_result()
    {
        var response = await Handle(NewProtocol(),
            "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"ping\"}");

        Assert.NotNull(response["result"]);
        Assert.Empty((JObject)response["result"]!);
    }

    [Fact]
    public void The_tools_changed_notification_carries_no_id()
    {
        // A notification, not a request: an id would have the client waiting for a reply that the
        // server is never going to send.
        var notification = JObject.Parse(McpProtocol.ToolsListChanged());

        Assert.Equal("2.0", (string?)notification["jsonrpc"]);
        Assert.Equal("notifications/tools/list_changed", (string?)notification["method"]);
        Assert.Null(notification["id"]);
    }
}
