using Newtonsoft.Json.Linq;
using Nlabs.ClaudeCodeVsPackage.Bridge.Mcp;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

public class McpHttpTransportTests
{
    private const string Token = "sekiz-on-alti-luk-oturum-jetonu";

    // A tiny catalog so the transport has a real McpProtocol to hand a request to. Its one tool is
    // enough to prove tools/list flows through - the point the native /ide path could not deliver.
    private sealed class OneToolCatalog : IMcpToolCatalog
    {
        public IReadOnlyList<McpToolDefinition> Tools { get; } = new[]
        {
            new McpToolDefinition("findSymbols", "Roslyn symbol search", new JObject()),
        };

        public Task<JObject> CallAsync(string name, JObject arguments, CancellationToken cancellationToken)
            => Task.FromResult(new JObject { ["ok"] = true });
    }

    private static McpHttpTransport Transport()
        => new McpHttpTransport(new McpProtocol(new OneToolCatalog(), "vs", "0.4.0"), Token);

    private static McpHttpRequest Post(string body, string? auth = "Bearer " + Token, string? origin = null, bool local = true, string path = "/mcp")
        => new McpHttpRequest("POST", path, auth, origin, local, body, body?.Length ?? 0);

    private static Task<McpHttpResponse> Handle(McpHttpRequest r) => Transport().HandleAsync(r, CancellationToken.None);

    [Fact]
    public async Task Tools_list_flows_through_where_the_native_path_could_not()
    {
        var r = await Handle(Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}"));

        Assert.Equal(200, r.Status);
        Assert.Equal("application/json", r.ContentType);
        var tools = (JArray)JObject.Parse(r.Body)["result"]!["tools"]!;
        Assert.Equal("findSymbols", (string?)tools[0]!["name"]);
    }

    [Fact]
    public async Task A_notification_is_accepted_with_no_body()
    {
        var r = await Handle(Post("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));

        Assert.Equal(202, r.Status);
        Assert.Equal(string.Empty, r.Body);
    }

    [Fact]
    public async Task A_request_carrying_Origin_is_a_browser_and_is_refused()
    {
        var r = await Handle(Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", origin: "http://evil.example"));
        Assert.Equal(403, r.Status);
    }

    [Fact]
    public async Task A_non_loopback_caller_is_refused()
    {
        var r = await Handle(Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", local: false));
        Assert.Equal(403, r.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer wrong-token")]
    [InlineData("sekiz-on-alti-luk-oturum-jetonu")]   // right value, but not a Bearer
    public async Task The_wrong_or_missing_token_is_401(string? auth)
    {
        var r = await Handle(Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", auth: auth));
        Assert.Equal(401, r.Status);
    }

    [Fact]
    public async Task Origin_is_weighed_before_the_token_so_an_outsider_learns_nothing()
    {
        // A browser with no token must look the same as a browser with a wrong one: 403, not 401.
        var r = await Handle(Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", auth: null, origin: "http://evil.example"));
        Assert.Equal(403, r.Status);
    }

    [Fact]
    public async Task Another_path_is_404()
    {
        var r = await Handle(Post("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", path: "/permission"));
        Assert.Equal(404, r.Status);
    }

    [Fact]
    public async Task A_verb_that_is_not_POST_or_GET_is_405()
    {
        var r = await Handle(new McpHttpRequest("DELETE", "/mcp", "Bearer " + Token, null, true, null, 0));
        Assert.Equal(405, r.Status);
    }

    [Fact]
    public async Task A_GET_is_granted_as_an_event_stream_for_the_socket_layer_to_hold_open()
    {
        var r = await Handle(new McpHttpRequest("GET", "/mcp", "Bearer " + Token, null, true, null, 0));
        Assert.True(r.IsEventStream);
    }

    [Fact]
    public async Task A_body_past_the_ceiling_is_413_before_it_is_parsed()
    {
        var r = await Handle(new McpHttpRequest("POST", "/mcp", "Bearer " + Token, null, true, "{}", 2L * 1024 * 1024));
        Assert.Equal(413, r.Status);
    }

    [Fact]
    public async Task An_empty_POST_body_is_400()
    {
        var r = await Handle(Post(""));
        Assert.Equal(400, r.Status);
    }
}
