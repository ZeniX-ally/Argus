using System.Net;
using System.Text;
using System.Text.Json;

namespace FctAggregator;

/// <summary>
/// 聚合端 HTTP 接收器（Phase 2 增强：B2 双通道方案）。
///
/// 机台端 AgentPusher http 直推模式的接收侧：机台把结构化 JSON POST 到聚合端，
/// fail 与 fail-{seq}.json 文件内容完全相同（顶层 machine/type/seq/ts/data，
/// data 含 station_id/model/…/has_fail_items/file_size），心跳为 machine/type/ts/last_seq/queued。
/// 本类不直接依赖 AggWatcher，由 AggCenterForm 构造时注入两个回调绑定（参数为原始 JSON 字符串）。
///
/// 路由（兼容机台端实际行为 + 契约文档路径）：
///   POST /                按 body 顶层 type 分发（机台端把两类请求都 POST 到 AggHttpUrl 根路径）
///   POST /api/fail        fail 处理
///   POST /api/heartbeat   heartbeat 处理
///   其他路径 404；非 POST 405；body 超 1MB 413；非法 JSON / 根路径 type 不明 400。
///
/// 响应策略：fail/heartbeat 处理一律回 200（回调异常也回 200 —— 机台端不因聚合端
/// 逻辑错误重推，重推压力由 (machine, seq) 幂等兜底）；协议错误（路径/方法/超限/坏 JSON）回 4xx。
/// </summary>
public class HttpIngest : IDisposable
{
    private const int MaxBodyBytes = 1024 * 1024;   // body 上限 1MB，超限 413
    private const int ReadChunk = 16384;

    private readonly int _port;
    private readonly Action<string> _onFail;
    private readonly Action<string> _onHeartbeat;

    private readonly object _lock = new();
    private HttpListener? _listener;
    private Thread? _acceptThread;
    private long _receivedCount;

    /// <summary>构造并绑定回调：onFail 收 fail JSON（机台 POST 的原始字符串），onHeartbeat 收心跳 JSON。</summary>
    public HttpIngest(int port, Action<string> onFail, Action<string> onHeartbeat)
    {
        _port = port;
        _onFail = onFail;
        _onHeartbeat = onHeartbeat;
    }

    /// <summary>HttpListener 是否成功开启（Start 失败时 false，GUI 据此显示失败状态）。</summary>
    public bool Listening
    {
        get { lock (_lock) return _listener != null && _listener.IsListening; }
    }

    /// <summary>收到的有效请求数（统计用：fail/heartbeat 路由命中且 body 合法的请求才计数）。</summary>
    public long ReceivedCount => Interlocked.Read(ref _receivedCount);

    /// <summary>启动监听：后台线程 accept；失败只记 ERROR 日志不抛异常（端口被占/无权限时 GUI 仍可运行）。
    /// 前缀组合按序回退：Windows(http.sys) 支持同监听器多前缀，Linux(managed socket 实现)
    /// 只允许一个绑定，"+ 与 127.0.0.1 同端口"会 Address already in use → 降级为单个前缀。</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_listener != null) return;
            var attempts = new[]
            {
                new[] { $"http://+:{_port}/", $"http://127.0.0.1:{_port}/" },  // 契约要求：全网卡 + 回环
                new[] { $"http://+:{_port}/" },                                 // 全网卡（含回环）
                new[] { $"http://127.0.0.1:{_port}/" },                         // 无 URL ACL 权限时的本机兜底
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

    /// <summary>停止监听并 Join 后台线程（幂等；Stop 后不再收新请求，在途请求正常收尾）。</summary>
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

    // ==================== accept 循环 ====================

    /// <summary>阻塞 GetContext + 每请求 Task.Run 处理（accept 循环绝不被单个慢请求阻塞）。</summary>
    private void AcceptLoop()
    {
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = _listener!.GetContext(); }
            catch { break; }   // 监听器被 Stop/Close：GetContext 抛异常，正常退出
            _ = Task.Run(() => Handle(ctx));
        }
    }

    // ==================== 单请求处理 ====================

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            using var resp = ctx.Response;

            if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                resp.StatusCode = 405;   // Method Not Allowed
                return;
            }

            // 读 body（限 1MB，超限 413 直接拒）
            string body;
            try { body = ReadBody(ctx.Request); }
            catch (BodyTooLargeException)
            {
                resp.StatusCode = 413;
                return;
            }

            // 校验 JSON 合法性并取顶层 type（路由用）
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

            // 路由：显式路径优先；根路径按 body type 分发（机台端两类请求都 POST 根路径）
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            string? route = null;
            if (path == "/api/fail") route = "fail";
            else if (path == "/api/heartbeat") route = "heartbeat";
            else if (path == "/" || path == "")
                route = string.Equals(type, "fail", StringComparison.OrdinalIgnoreCase) ? "fail"
                      : string.Equals(type, "heartbeat", StringComparison.OrdinalIgnoreCase) ? "heartbeat" : null;

            if (route == null)
            {
                // 根路径但 type 不明 = 机台端发错了内容 → 400；其他未知路径 → 404
                resp.StatusCode = path == "/" || path == "" ? 400 : 404;
                return;
            }

            Interlocked.Increment(ref _receivedCount);
            try
            {
                if (route == "fail") _onFail(body);
                else _onHeartbeat(body);
            }
            catch (Exception ex)
            {
                // 聚合端逻辑错误绝不回 4xx/5xx：机台端不重推，重复风险交给 (machine, seq) 幂等兜底
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

    private sealed class BodyTooLargeException : Exception { }
}
