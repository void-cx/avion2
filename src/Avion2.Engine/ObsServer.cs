using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Avion2.Engine;

/// <summary>A file the OBS page loads (the page itself, a font).</summary>
public sealed record ObsFile(string ContentType, byte[] Body);

/// <summary>
/// The OBS overlay's web server: answers <c>/overlay</c> (the page), <c>/state</c> (the latest numbers, JSON) and
/// the page's own files, on 127.0.0.1 only, so nothing outside this PC can connect. Requests whose Host is not
/// localhost are refused, so a web page can't reach it through a DNS name pointing at 127.0.0.1. GET only, one
/// request per connection.
/// </summary>
public sealed class ObsServer : IDisposable
{
    public const int DefaultPort = 47421;

    private readonly TcpListener _listener;
    private readonly IReadOnlyDictionary<string, ObsFile> _files;
    private readonly CancellationTokenSource _cts = new();
    private volatile byte[] _state = "{}"u8.ToArray();

    /// <param name="files">Path (<c>/overlay</c>, <c>/fonts/x.ttf</c>) → file.</param>
    public ObsServer(int port, IReadOnlyDictionary<string, ObsFile> files)
    {
        _files = files;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    /// <summary>The port in use (the given one, or the one picked for port 0).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Starts listening; throws a <see cref="SocketException"/> when the port is taken.</summary>
    public void Start()
    {
        _listener.Start();
        _ = AcceptAsync(_cts.Token);
    }

    /// <summary>The JSON <c>/state</c> answers from now on.</summary>
    public void Publish(string json) => _state = Encoding.UTF8.GetBytes(json);

    private async Task AcceptAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancel);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = ServeAsync(client, cancel);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancel)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var stream = client.GetStream();
                string? head = await ReadHeadAsync(stream, timeout.Token);
                var (status, type, body) = head is null ? (400, "text/plain", "Bad request"u8.ToArray()) : Answer(head);
                await WriteAsync(stream, status, type, body, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // the browser went away
            }
        }
    }

    /// <summary>The request line and headers, or null when they're too long or the connection closed early.</summary>
    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken cancel)
    {
        var buffer = new byte[8192];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), cancel);
            if (read == 0)
            {
                return null;
            }

            length += read;
            string text = Encoding.ASCII.GetString(buffer, 0, length);
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                return text[..end];
            }
        }

        return null;
    }

    /// <summary>Status, content type and body for a request head.</summary>
    public (int Status, string ContentType, byte[] Body) Answer(string head)
    {
        var lines = head.Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length != 3 || !request[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            return (400, "text/plain", "Bad request"u8.ToArray());
        }

        string? host = lines.Skip(1)
            .Where(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l[5..].Trim())
            .FirstOrDefault();
        if (host is null || !IsLocalHost(host))
        {
            return (403, "text/plain", "Only for OBS on this PC: open it as http://localhost"u8.ToArray());
        }

        if (request[0] != "GET")
        {
            return (405, "text/plain", "GET only"u8.ToArray());
        }

        string path = request[1].Split('?', 2)[0];
        if (path == "/state")
        {
            return (200, "application/json; charset=utf-8", _state);
        }

        if (_files.TryGetValue(path == "/" ? "/overlay" : path, out var file))
        {
            return (200, file.ContentType, file.Body);
        }

        return (404, "text/plain", "Not found"u8.ToArray());
    }

    /// <summary>"localhost", "127.0.0.1", either with a port.</summary>
    private static bool IsLocalHost(string host)
    {
        int colon = host.LastIndexOf(':');
        string name = colon >= 0 ? host[..colon] : host;
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name == "127.0.0.1";
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string type, byte[] body, CancellationToken cancel)
    {
        string reason = status switch { 200 => "OK", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Bad Request" };
        string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancel);
        await stream.WriteAsync(body, cancel);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
