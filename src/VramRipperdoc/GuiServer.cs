using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace VramRipperdoc;

/// <summary>
/// A tiny HTTP/1.1 server for the app window, on 127.0.0.1 only. Hand-written on TcpListener because HttpListener needs
/// Windows' http.sys, which Wine doesn't have. Every /api call needs the session token and a local Host header, so no
/// other web page can use it; the only page is the one built into the exe, and nothing is ever served from disk.
/// </summary>
public sealed class GuiServer
{
    const int MaxBody = 1 << 20, MaxHead = 32 << 10;
    readonly GuiSession _app;
    readonly TcpListener _listener;
    readonly byte[] _token;
    readonly byte[] _page;
    volatile bool _stopping;
    int _streams;
    long _lastStream = DateTime.UtcNow.Ticks;
    volatile bool _everStreamed;
    readonly System.Collections.Concurrent.ConcurrentDictionary<TcpClient, byte> _open = new();

    public int Port { get; }
    public string Token { get; }
    public string Url => $"http://127.0.0.1:{Port}/?t={Token}";
    /// <summary>Event streams open right now: the app window keeps one open while it is open.</summary>
    public int Streams => Volatile.Read(ref _streams);
    public bool EverConnected => _everStreamed;
    /// <summary>How long no event stream has been open.</summary>
    public TimeSpan Idle => Streams > 0 ? TimeSpan.Zero : DateTime.UtcNow - new DateTime(Interlocked.Read(ref _lastStream), DateTimeKind.Utc);

    /// <param name="port">0 = any free port.</param>
    public GuiServer(GuiSession app, int port, string token)
    {
        _app = app;
        Token = token;
        _token = Encoding.UTF8.GetBytes(token);
        _page = LoadPage();
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        new Thread(AcceptLoop) { IsBackground = true, Name = "RipperDoc accept" }.Start();
    }

    static byte[] LoadPage()
    {
        using var s = typeof(GuiServer).Assembly.GetManifestResourceStream("app.html");
        if (s == null) return Encoding.UTF8.GetBytes("<!doctype html><title>VRAM RipperDoc</title><p>The app page is missing from this build.</p>");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Stops listening and closes every open connection (a browser keeps idle ones open for minutes).</summary>
    public void Stop()
    {
        _stopping = true;
        try { _listener.Stop(); } catch { }
        foreach (var c in _open.Keys) try { c.Close(); } catch { }
    }

    void AcceptLoop()
    {
        while (!_stopping)
        {
            TcpClient c;
            try { c = _listener.AcceptTcpClient(); }
            catch { if (_stopping) return; Thread.Sleep(50); continue; }
            _open[c] = 0;
            if (_stopping) { c.Close(); return; }
            new Thread(() => { try { Serve(c); } finally { _open.TryRemove(c, out _); } }) { IsBackground = true, Name = "RipperDoc http" }.Start();
        }
    }

    sealed record Request(string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers, byte[] Body, bool KeepAlive);

    sealed class HttpError(int status, string message) : Exception(message) { public int Status => status; }

    void Serve(TcpClient client)
    {
        using (client)
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                stream.ReadTimeout = 120_000; // an idle keep-alive connection gives up after two minutes
                var reader = new Reader(stream);
                while (!_stopping)
                {
                    Request? req;
                    try { req = Read(reader); }
                    catch (HttpError e)
                    {
                        Send(stream, e.Status, GuiSession.Error(e.Status, e.Message).Body, "application/json; charset=utf-8", keepAlive: false);
                        // The client may still be sending the body we refused: read it away for a moment, so it gets
                        // this answer instead of a reset connection.
                        try
                        {
                            client.Client.Shutdown(SocketShutdown.Send);
                            stream.ReadTimeout = 2000;
                            var sink = new byte[64 << 10];
                            for (long left = 4L << 20; left > 0;) { int n = stream.Read(sink, 0, sink.Length); if (n <= 0) break; left -= n; }
                        }
                        catch { }
                        return;
                    }
                    if (req == null || !Handle(client, stream, req) || !req.KeepAlive) return;
                }
            }
            catch { } // the window went away mid-request: nothing to tell anyone
    }

    static Request? Read(Reader reader)
    {
        var head = reader.ReadHead();
        if (head == null) return null;
        var lines = head.TrimStart('\r', '\n').Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.")) throw new HttpError(400, "Not an HTTP request.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) throw new HttpError(400, "A header line couldn't be read.");
            var name = line[..colon].Trim();
            if (headers.ContainsKey(name) && name.Equals("Host", StringComparison.OrdinalIgnoreCase)) throw new HttpError(400, "Two Host headers.");
            headers[name] = line[(colon + 1)..].Trim();
        }
        if (headers.ContainsKey("Transfer-Encoding")) throw new HttpError(501, "Send the body with a Content-Length.");
        long length = 0;
        if (headers.TryGetValue("Content-Length", out var cl) && (!long.TryParse(cl, out length) || length < 0)) throw new HttpError(400, "Bad Content-Length.");
        if (length > MaxBody) throw new HttpError(413, "That request is too big.");
        var body = reader.ReadBody((int)length);

        var target = first[1];
        int q = target.IndexOf('?');
        var path = q < 0 ? target : target[..q];
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (q >= 0)
            foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
                query.TryAdd(key, eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')));
            }
        bool keepAlive = first[2] == "HTTP/1.1"
            ? !(headers.GetValueOrDefault("Connection") ?? "").Contains("close", StringComparison.OrdinalIgnoreCase)
            : (headers.GetValueOrDefault("Connection") ?? "").Contains("keep-alive", StringComparison.OrdinalIgnoreCase);
        return new Request(first[0], path, query, headers, body, keepAlive);
    }

    bool TokenOk(string? given) => given != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), _token);

    // Only our own address: a web page that points some other host name at 127.0.0.1 (DNS rebinding) is refused.
    bool HostOk(Request r) => r.Headers.TryGetValue("Host", out var host)
        && (host.Equals($"127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase) || host.Equals($"localhost:{Port}", StringComparison.OrdinalIgnoreCase));

    /// <summary>Answers one request. False: the connection is done (event stream ended).</summary>
    bool Handle(TcpClient client, NetworkStream s, Request r)
    {
        const string JsonType = "application/json; charset=utf-8";
        bool api = r.Path.StartsWith("/api/", StringComparison.Ordinal);
        if (r.Path != "/" && !api) { Send(s, 404, Encoding.UTF8.GetBytes("Not found."), "text/plain; charset=utf-8", r.KeepAlive); return true; }
        if (!HostOk(r))
        {
            Send(s, 403, api ? GuiSession.Error(403, "Wrong Host: use 127.0.0.1.").Body : Encoding.UTF8.GetBytes("Wrong Host: use 127.0.0.1."),
                api ? JsonType : "text/plain; charset=utf-8", r.KeepAlive);
            return true;
        }
        if (r.Path == "/")
        {
            if (r.Method != "GET") { Send(s, 405, Encoding.UTF8.GetBytes("Use GET."), "text/plain; charset=utf-8", r.KeepAlive); return true; }
            if (!TokenOk(r.Query.GetValueOrDefault("t")))
            {
                Send(s, 403, Encoding.UTF8.GetBytes("Open VRAM RipperDoc from its own window: this address needs the key it was started with."), "text/plain; charset=utf-8", r.KeepAlive);
                return true;
            }
            Send(s, 200, _page, "text/html; charset=utf-8", r.KeepAlive,
                "Content-Security-Policy: default-src 'self' 'unsafe-inline' data: blob:; frame-ancestors 'none'\r\nX-Frame-Options: DENY\r\n");
            return true;
        }
        if (r.Path == "/api/events")
        {
            if (!TokenOk(r.Query.GetValueOrDefault("t") ?? r.Headers.GetValueOrDefault("X-RipperDoc-Token")))
            { Send(s, 403, GuiSession.Error(403, "Wrong or missing key.").Body, JsonType, r.KeepAlive); return true; }
            if (r.Method != "GET") { Send(s, 405, GuiSession.Error(405, "Use GET for /api/events.").Body, JsonType, r.KeepAlive); return true; }
            Events(client, s);
            return false;
        }
        if (!TokenOk(r.Headers.GetValueOrDefault("X-RipperDoc-Token")))
        { Send(s, 403, GuiSession.Error(403, "Wrong or missing key.").Body, JsonType, r.KeepAlive); return true; }
        GuiSession.Answer a;
        try { a = _app.Handle(r.Method, r.Path, r.Body); }
        catch (Exception ex) { a = GuiSession.Error(500, "Something went wrong: " + ex.Message); }
        Send(s, a.Status, a.Body, JsonType, r.KeepAlive);
        return true;
    }

    /// <summary>Server-sent events: "job" twice a second while a job runs, "changed" when the state changed, a ping every 5 s.</summary>
    void Events(TcpClient client, NetworkStream s)
    {
        Interlocked.Increment(ref _streams);
        _everStreamed = true;
        _app.WindowClosing = false;
        try
        {
            Write(s, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n"
                + ": connected\n\nevent: changed\ndata: {}\n\n");
            int seen = _app.Version;
            var ping = Stopwatch.StartNew();
            Stopwatch? job = null;
            while (!_stopping)
            {
                _app.WaitForChange(seen, 250);
                // The window closed: the socket reads as ready with nothing to read.
                if (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0) break;
                int v = _app.Version;
                bool changed = v != seen;
                if (changed) { seen = v; Write(s, "event: changed\ndata: {}\n\n"); }
                // Right away when something changed (a job that just started), else about twice a second.
                if ((changed || job == null || job.ElapsedMilliseconds >= 450) && _app.BusyJson() is { } j)
                {
                    Write(s, "event: job\ndata: " + Encoding.UTF8.GetString(j) + "\n\n");
                    (job ??= new Stopwatch()).Restart();
                }
                if (ping.ElapsedMilliseconds >= 5000) { Write(s, ": ping\n\n"); ping.Restart(); }
            }
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _lastStream, DateTime.UtcNow.Ticks);
            Interlocked.Decrement(ref _streams);
        }
    }

    static void Write(Stream s, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        s.Write(bytes, 0, bytes.Length);
        s.Flush();
    }

    static void Send(Stream s, int status, byte[] body, string type, bool keepAlive, string extraHeaders = "")
    {
        string reason = status switch
        {
            200 => "OK", 202 => "Accepted", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed",
            409 => "Conflict", 413 => "Payload Too Large", 500 => "Internal Server Error", 501 => "Not Implemented", _ => "Error",
        };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\n"
            + $"X-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n{extraHeaders}Connection: {(keepAlive ? "keep-alive" : "close")}\r\n\r\n";
        var h = Encoding.ASCII.GetBytes(head);
        s.Write(h, 0, h.Length);
        s.Write(body, 0, body.Length);
        s.Flush();
    }

    /// <summary>Reads a request's head up to the blank line and its body, keeping what arrived early for the next request.</summary>
    sealed class Reader(Stream s)
    {
        readonly byte[] _buf = new byte[MaxHead + 4096];
        int _start, _end;

        public string? ReadHead()
        {
            if (_start == _end) _start = _end = 0;
            int scanFrom = _start;
            while (true)
            {
                int i = _buf.AsSpan(scanFrom, _end - scanFrom).IndexOf("\r\n\r\n"u8);
                if (i >= 0)
                {
                    int stop = scanFrom + i;
                    var head = Encoding.Latin1.GetString(_buf, _start, stop - _start);
                    _start = stop + 4;
                    return head;
                }
                if (_end - _start >= MaxHead) throw new HttpError(400, "The request headers are too big.");
                scanFrom = Math.Max(_start, _end - 3);
                if (_end == _buf.Length) { Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start); scanFrom -= _start; _end -= _start; _start = 0; }
                int n = s.Read(_buf, _end, _buf.Length - _end);
                if (n == 0) return null;
                _end += n;
            }
        }

        public byte[] ReadBody(int length)
        {
            var body = new byte[length];
            int have = Math.Min(length, _end - _start);
            Buffer.BlockCopy(_buf, _start, body, 0, have);
            _start += have;
            if (_start == _end) _start = _end = 0;
            while (have < length)
            {
                int n = s.Read(body, have, length - have);
                if (n == 0) throw new IOException("The connection closed mid-request.");
                have += n;
            }
            return body;
        }
    }
}
