using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nlabs.ClaudeCodeVsPackage.Mcp;

// The MCP server that Claude Code launches (stdio). It bridges to the Visual Studio
// extension's local WebSocket and exposes VS actions as MCP tools.
//
// Add it to Claude Code with:
//   claude mcp add vs-bridge -e NLABS_BRIDGE_PORT=<port> -e NLABS_BRIDGE_TOKEN=<token> \
//       -- dotnet <path-to>/nlabs-claude-code-bridge-mcp.dll
// The port and token come from the extension's "Tools > Restart Local Bridge" dialog.

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the JSON-RPC protocol, so ALL logging must go to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<BridgeClient>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
