using System.Net;
using System.Text;

namespace Aurora.Tests.Support;

/// <summary>
/// A stand-in for an HTTP service, on loopback.
/// </summary>
/// <remarks>
/// The same principle as <see cref="FakeDiscord"/>: the fake is at the far end of a real socket, so
/// what is under test is a real client sending real requests and reading real responses. What is
/// replaced is only whose server answers them.
/// <para>
/// It records what it was asked, because "the request was refused" and "the request was sent and
/// the far end said no" are the two things a boundary has to keep apart, and they look identical
/// from the caller's side.
/// </para>
/// </remarks>
public sealed class FakeHttpService : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<Recorded> _seen = [];
    private readonly Queue<Reply> _replies = new();

    public FakeHttpService()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();

        _ = Task.Run(ServeAsync);
    }

    public int Port { get; }

    public Uri Url(string path) => new($"http://127.0.0.1:{Port}{path}");

    /// <summary>What arrived, in order. Empty means the client refused before sending.</summary>
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

    /// <summary>Queues one answer. Answers are given in the order they were queued.</summary>
    public FakeHttpService Answer(
        int status, string body = "{}", params (string Name, string Value)[] headers)
    {
        lock (_replies)
        {
            _replies.Enqueue(new Reply(status, body, headers));
        }

        return this;
    }

    public sealed record Recorded(
        string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body);

    private sealed record Reply(int Status, string Body, (string Name, string Value)[] Headers);

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception stopping)
                when (stopping is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            var headers = context.Request.Headers.AllKeys
                .Where(key => key is not null)
                .ToDictionary(key => key!.ToLowerInvariant(), key => context.Request.Headers[key] ?? "");

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();

            lock (_seen)
            {
                _seen.Add(new Recorded(
                    context.Request.HttpMethod, context.Request.Url?.AbsolutePath ?? "", headers, body));
            }

            Reply reply;

            lock (_replies)
            {
                reply = _replies.Count > 0 ? _replies.Dequeue() : new Reply(200, "{}", []);
            }

            context.Response.StatusCode = reply.Status;

            foreach ((var name, var value) in reply.Headers)
            {
                context.Response.Headers[name] = value;
            }

            var bytes = Encoding.UTF8.GetBytes(reply.Body);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already gone.
        }
    }
}
