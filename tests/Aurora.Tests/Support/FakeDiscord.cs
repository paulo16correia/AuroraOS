using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aurora.Tests.Support;

/// <summary>
/// A stand-in for Discord's REST API, on loopback.
/// </summary>
/// <remarks>
/// The fake is at the outermost boundary and nowhere else: the plugin under test is the real
/// plugin, running as a real subprocess, building real requests and parsing real responses. What
/// is replaced is the far end of the socket, because the alternative is a suite that only passes
/// when somebody's Discord server happens to be up and that quietly posts messages into it.
/// <para>
/// It records what it was asked, so a test can check that Aurora sent what it said it sent — a
/// send that returns success and never reached the API would otherwise look identical to one that
/// worked.
/// </para>
/// </remarks>
public sealed class FakeDiscord : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Recorded> _seen = [];
    private readonly Dictionary<string, Func<Recorded, (int Status, string Body)>> _routes = new(StringComparer.Ordinal);

    public FakeDiscord()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();

        _ = Task.Run(ServeAsync);
    }

    public int Port { get; }

    /// <summary>Frames to push once a gateway client has identified, in order.</summary>
    public List<string> GatewayDispatches { get; } = [];

    /// <summary>What the plugin sent up the gateway: its identify, its heartbeats.</summary>
    public List<JsonNode> GatewaySent { get; } = [];

    private readonly TaskCompletionSource _identified =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the plugin has identified on the gateway.</summary>
    public Task Identified => _identified.Task;

    public string BaseUrl => $"http://127.0.0.1:{Port}/api/v10";

    /// <summary>Every request the plugin actually made, in order.</summary>
    public IReadOnlyList<Recorded> Seen
    {
        get
        {
            lock (_seen)
            {
                return [.. _seen];
            }
        }
    }

    /// <summary>How many messages the fake accepted. The count a duplicate would change.</summary>
    public int Posted => Seen.Count(r => r.Method == "POST" && r.Path.EndsWith("/messages", StringComparison.Ordinal));

    public sealed record Recorded(string Method, string Path, string Query, string Body)
    {
        public JsonNode? Json => string.IsNullOrEmpty(Body) ? null : JsonNode.Parse(Body);
    }

    /// <summary>Answers <paramref name="method"/> and <paramref name="path"/> with a fixed body.</summary>
    public FakeDiscord Route(string method, string path, object body, int status = 200)
    {
        _routes[$"{method} {path}"] = _ => (status, JsonSerializer.Serialize(body));
        return this;
    }

    /// <summary>Answers with whatever the handler decides, so a route can behave differently by call.</summary>
    public FakeDiscord Route(
        string method, string path, Func<Recorded, (int Status, string Body)> handler)
    {
        _routes[$"{method} {path}"] = handler;
        return this;
    }

    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception stopping) when (stopping is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            try
            {
                await AnswerAsync(context);
            }
            catch (Exception ignored) when (ignored is IOException or HttpListenerException)
            {
                // The plugin hung up. Nothing to do about it here.
            }
        }
    }

    private async Task AnswerAsync(HttpListenerContext context)
    {
        if (context.Request.IsWebSocketRequest)
        {
            await GatewayAsync(context);
            return;
        }

        var path = context.Request.Url!.AbsolutePath.Replace("/api/v10", "", StringComparison.Ordinal);
        using var reader = new StreamReader(context.Request.InputStream);
        var body = await reader.ReadToEndAsync();

        var recorded = new Recorded(
            context.Request.HttpMethod, path, context.Request.Url.Query, body);

        lock (_seen)
        {
            _seen.Add(recorded);
        }

        // The token must be present — a plugin that forgot it would otherwise pass every test
        // against a fake that does not care.
        var authorized = context.Request.Headers["Authorization"]?.StartsWith(
            "Bot ", StringComparison.Ordinal) ?? false;

        (var status, var answer) = !authorized
            ? (401, """{"message":"401: Unauthorized"}""")
            : _routes.TryGetValue($"{recorded.Method} {path}", out var handler)
                ? handler(recorded)
                : (404, """{"message":"Unknown Channel"}""");

        var bytes = Encoding.UTF8.GetBytes(answer);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    /// <summary>
    /// Discord's Gateway, enough of it to be identified with and dispatched from.
    /// </summary>
    /// <remarks>
    /// A real websocket on the wire: the plugin performs a real RFC 6455 handshake, masks its
    /// frames, and is disconnected if it gets any of that wrong. Faking the transport would have
    /// left the one piece of protocol code in this integration untested.
    /// </remarks>
    private async Task GatewayAsync(HttpListenerContext context)
    {
        HttpListenerWebSocketContext upgraded = await context.AcceptWebSocketAsync(subProtocol: null);
        WebSocket socket = upgraded.WebSocket;

        await SendAsync(socket, """{"op":10,"d":{"heartbeat_interval":45000}}""");

        var buffer = new byte[64 * 1024];
        var sequence = 0;

        while (socket.State == WebSocketState.Open && !_stopping.IsCancellationRequested)
        {
            WebSocketReceiveResult received;

            try
            {
                received = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), _stopping.Token);
            }
            catch (Exception ending) when (ending is WebSocketException or OperationCanceledException)
            {
                return;
            }

            if (received.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            JsonNode? frame = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, received.Count));

            if (frame is null)
            {
                continue;
            }

            lock (GatewaySent)
            {
                GatewaySent.Add(frame);
            }

            if (frame["op"]?.GetValue<int>() != 2)
            {
                continue;
            }

            await SendAsync(socket, JsonSerializer.Serialize(new
            {
                op = 0,
                s = ++sequence,
                t = "READY",
                d = new
                {
                    session_id = "session-1",
                    resume_gateway_url = $"ws://127.0.0.1:{Port}",
                    user = new { id = "999000999000999000", username = "aurora", bot = true },
                    guilds = Array.Empty<object>(),
                },
            }));

            _identified.TrySetResult();

            foreach (var dispatch in GatewayDispatches)
            {
                await SendAsync(socket, dispatch.Replace("\"s\":0", $"\"s\":{++sequence}", StringComparison.Ordinal));
            }
        }
    }

    private async Task SendAsync(WebSocket socket, string text) =>
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true,
            _stopping.Token);

    /// <summary>A MESSAGE_CREATE the gateway will push once the plugin has identified.</summary>
    public FakeDiscord PushesMessage(
        string id, string content, string authorId = "444444444444444444",
        string authorName = "paulo", bool authorIsBot = false)
    {
        GatewayDispatches.Add(JsonSerializer.Serialize(new
        {
            op = 0,
            s = 0,
            t = "MESSAGE_CREATE",
            d = new
            {
                id,
                channel_id = "222222222222222222",
                guild_id = "111111111111111111",
                content,
                timestamp = "2026-08-26T12:00:00+00:00",
                author = new { id = authorId, username = authorName, bot = authorIsBot },
                mentions = Array.Empty<object>(),
            },
        }));

        return this;
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Close();
        _stopping.Dispose();
    }
}
