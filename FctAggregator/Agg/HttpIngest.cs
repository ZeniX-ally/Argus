using System.Net;
using System.Text;
using System.Text.Json;

namespace FctAggregator;

public class HttpIngest : IDisposable
{
    private const int MaxBodyBytes = 1024 * 1024;
    private const int ReadChunk = 16384;
    public const string TokenHeader = "X-Agg-Token";

    private readonly int _port;
    private readonly Action<string> _onFail;
    private readonly Action<string> _onHeartbeat;
    private readonly string _token;

    private readonly object _lock = new();
    private HttpListener? _listener;
    private Thread? _acceptThread;
    private long _receivedCount;

    public HttpIngest(int port, Action<string> onFail, Action<string> onHeartbeat, string token = "")
    {
        _port = port;
        _onFail = onFail;
        _onHeartbeat = onHeartbeat;
        _token = token ?? "";
        if (_token.Length > 0) Logger.Info($"[HTTP 接收] 已启用 agg_token 鉴权（不匹配回 403）");
    }

    public bool Listening
    {
        get { lock (_lock) return _listener != null && _listener.IsListening; }
    }

    public long ReceivedCount => Interlocked.Read(ref _receivedCount);

    public void Start()
    {
        lock (_lock)
        {
            if (_listener != null) return;
            var attempts = new[]
            {
                new[] { $"http://+:{_port}/", $"http://127.0.0.1:{_port}/" },
                new[] { $"http://+:{_port}/" },
                new[] { $"http://127.0.0.1:{_port}/" },
            };
            foreach (var prefixes in attempts)
            {
                try
                {
                    var l = new HttpListener();
                    foreach (var p in prefixes) l.Prefixes.Add(p);
                    l.Start();
                    _listener = l;
                    _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "http-ingest" };
                    _acceptThread.Start();
                    Logger.Info($"[HTTP 接收] 已启动，监听 :{_port}（{string.Join(" ", prefixes)}）");
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[HTTP 接收] 前缀 '{string.Join("' '", prefixes)}' 启动失败: {ex.Message}");
                }
            }
            _listener = null;
            Logger.Error($"[HTTP 接收] 启动失败（端口 {_port} 被占用或缺少监听权限）");
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            var l = _listener;
            _listener = null;
            if (l != null)
            {
                try { l.Stop(); } catch { }
                try { l.Close(); } catch { }
            }
            var t = _acceptThread;
            _acceptThread = null;
            if (t != null)
            {
                try { t.Join(3000); } catch { }
            }
        }
    }

    public void Dispose()
    {
        try { Stop(); } catch { }
    }

    private void AcceptLoop()
    {
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = _listener!.GetContext(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            using var resp = ctx.Response;

            if (_token.Length > 0)
            {
                var got = ctx.Request.Headers[TokenHeader] ?? "";
                if (got.Length == 0 || !FixedTimeEqualsToken(got, _token))
                {
                    Logger.Warning($"[HTTP 接收] 403: agg_token 缺失或不匹配（{ctx.Request.RemoteEndPoint}）");
                    resp.StatusCode = 403;
                    return;
                }
            }

            if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                resp.StatusCode = 405;
                return;
            }

            string body;
            try { body = ReadBody(ctx.Request); }
            catch (BodyTooLargeException)
            {
                resp.StatusCode = 413;
                return;
            }

            string? type;
            try
            {
                using var doc = JsonDocument.Parse(body);
                type = doc.RootElement.TryGetProperty("type", out var tv) ? tv.GetString() : null;
            }
            catch (JsonException ex)
            {
                Logger.Warning($"[HTTP 接收] body 不是合法 JSON: {ex.Message}");
                resp.StatusCode = 400;
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "";
            string? route = null;
            if (path == "/api/fail") route = "fail";
            else if (path == "/api/heartbeat") route = "heartbeat";
            else if (path == "/" || path == "")
                route = string.Equals(type, "fail", StringComparison.OrdinalIgnoreCase) ? "fail"
                      : string.Equals(type, "heartbeat", StringComparison.OrdinalIgnoreCase) ? "heartbeat" : null;

            if (route == null)
            {
                resp.StatusCode = path == "/" || path == "" ? 400 : 404;
                return;
            }

            Interlocked.Increment(ref _receivedCount);
            try
            {
                if (route == "fail") _onFail(body);
                else _onHeartbeat(body);
            }
            catch (AggIngestException ex)
            {
                Logger.Error($"[HTTP 接收] fail 入库失败 machine={ex.Machine} seq={ex.Seq}: {ex.Message}");
                resp.StatusCode = 500;
                return;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[HTTP 接收] {route} 回调异常（仍回 200）: {ex.Message}");
            }
            resp.StatusCode = 200;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[HTTP 接收] 请求处理异常: {ex.Message}");
            try { ctx.Response.StatusCode = 400; ctx.Response.Close(); } catch { }
        }
    }

    private static string ReadBody(HttpListenerRequest req)
    {
        using var ms = new MemoryStream();
        var buf = new byte[ReadChunk];
        while (true)
        {
            int n = req.InputStream.Read(buf, 0, buf.Length);
            if (n <= 0) break;
            if (ms.Length + n > MaxBodyBytes) throw new BodyTooLargeException();
            ms.Write(buf, 0, n);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static bool FixedTimeEqualsToken(string a, string b)
    {
        var ba = System.Text.Encoding.UTF8.GetBytes(a);
        var bb = System.Text.Encoding.UTF8.GetBytes(b);
        if (ba.Length != bb.Length) return false;
        int diff = 0;
        for (int i = 0; i < ba.Length; i++) diff |= ba[i] ^ bb[i];
        return diff == 0;
    }

    private sealed class BodyTooLargeException : Exception { }
}
