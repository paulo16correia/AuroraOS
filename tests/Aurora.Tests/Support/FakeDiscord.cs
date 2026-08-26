using System.Net;
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
