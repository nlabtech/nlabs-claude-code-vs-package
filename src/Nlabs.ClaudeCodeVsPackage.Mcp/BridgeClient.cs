using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Nlabs.ClaudeCodeVsPackage.Mcp;

/// <summary>
/// The Claude Code side of the bridge: a WebSocket client to the Visual Studio extension.
///
/// It connects to 127.0.0.1 with the session token (both handed to it via environment
/// variables that the developer copies from the extension's "Restart Local Bridge"
/// command), then sends { id, type, payload } requests and correlates each reply back to
/// its request by id - the same correlation-by-id principle the server uses, because
/// replies can interleave.
/// </summary>
public sealed class BridgeClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private Task? _receiveLoop;
    private bool _connected;

    private static string Port => Environment.GetEnvironmentVariable("NLABS_BRIDGE_PORT") ?? "";
    private static string Token => Environment.GetEnvironmentVariable("NLABS_BRIDGE_TOKEN") ?? "";

    public async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_connected) return;

        await _connectGate.WaitAsync(ct);
        try
        {
            if (_connected) return;

            if (string.IsNullOrEmpty(Port) || string.IsNullOrEmpty(Token))
            {
                throw new InvalidOperationException(
                    "Set NLABS_BRIDGE_PORT and NLABS_BRIDGE_TOKEN (from the extension's 'Restart Local Bridge' command).");
            }

            // No Origin header (ClientWebSocket sends none), Host is loopback, Bearer token,
            // and a subprotocol the server echoes back - matching the server's handshake rules.
            _socket.Options.SetRequestHeader("Authorization", "Bearer " + Token);
            _socket.Options.AddSubProtocol("nlabs-bridge");

            await _socket.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/"), ct);
            _connected = true;
            _receiveLoop = Task.Run(ReceiveLoopAsync);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task<JsonNode?> SendAsync(string type, JsonObject? payload, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);

        var id = Guid.NewGuid().ToString("n");
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var message = new JsonObject
        {
            ["id"] = id,
            ["type"] = type,
            ["payload"] = payload ?? new JsonObject(),
        };

        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);

        await using (ct.Register(() => tcs.TrySetCanceled(ct)))
        {
            return await tcs.Task;
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[8192];
        var builder = new StringBuilder();

        while (_socket.State == WebSocketState.Open)
        {
            builder.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return;
                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            var node = JsonNode.Parse(builder.ToString());
            var id = node?["id"]?.GetValue<string>();
            if (id != null && _pending.TryRemove(id, out var tcs))
            {
                tcs.TrySetResult(node);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
        }
        catch
        {
            // closing best-effort
        }

        _socket.Dispose();
    }
}
