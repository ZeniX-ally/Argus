using System.Net;
using System.Text;
using System.Text.Json;

namespace FctAggregator;

/// <summary>
/// 聚合端纯 Web 服务（Phase 3 新增：聚合端 Web 模式）。
///
/// 机台上跑 `Argus.exe agg --web`（无窗体常驻），本服务同时承载：
///   1. 机台推送入口（与 HttpIngest 同构）：POST /api/fail、/api/heartbeat、POST / 按 body type 分发；
///   2. 浏览器看板（GET）：机台状态卡片、FAIL 明细表、XML 报告下载、CSV 导出、健康检查。
///
/// 路由表：
///   GET  /                    内嵌 HTML 看板页（深色主题，无外部资源，可直接在浏览器打开）
///   GET  /api/machines        机台状态列表 JSON（AggMachineStatus 数组）
///   GET  /api/fails?limit&machine  FAIL 明细 JSON（AggFailRow 数组；limit 默认 200，上限 1000）
///   GET  /api/file?id=|path=  XML 报告下载（白名单校验，见 <see cref="ResolveFile"/>）
///   GET  /api/list?path=     目录浏览：resultsRoot 白名单内子目录列表（见 <see cref="ResolveDir"/>）
///   GET  /api/export.csv?limit&machine  FAIL 明细 CSV（UTF-8 BOM + CWE-1236 公式注入防护）
///   GET  /api/health          {"ok":true}（部署排查用）
///   POST /api/fail            机台 fail 推送
///   POST /api/heartbeat       机台心跳推送
///   POST /                    按 body 顶层 type 分发（fail/heartbeat）
///   其他路径 404；方法不符 405；POST body 超 1MB 413；根路径 type 不明 / 非法 JSON 400。
///
/// 响应策略：fail/heartbeat 处理一律回 200（回调异常也回 200 —— 机台端不因聚合端
/// 逻辑错误重推，重复风险由 (machine, seq) 幂等兜底）；协议错误回 4xx。
///
/// JSON 序列化坑：AggFailRow / AggMachineStatus 都是 public **字段**（非属性），
/// System.Text.Json 默认不序列化字段，必须 <see cref="JsonOpts"/> 显式 IncludeFields。
/// </summary>
public class WebAggServer : IDisposable
{
    private const int MaxBodyBytes = 1024 * 1024;   // POST body 上限 1MB，超限 413
    private const int ReadChunk = 16384;
    private const int DefaultLimit = 200;           // /api/fails 与 /api/export.csv 默认行数
    private const int MaxLimit = 1000;              // limit 参数上限
    private const string ForbiddenText = "拒绝访问：文件不在允许目录内";
    private const string ForbiddenDirText = "拒绝访问：目录不在允许范围内";

    /// <summary>字段型模型类序列化必须 IncludeFields（AggFailRow/AggMachineStatus 都是 public 字段）。
    /// 命名保持默认（PascalCase 原字段名），页面 JS 按字段名取。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new() { IncludeFields = true };

    /// <summary>日志白名单文件（{BaseDir}/logs/app.log）：始终允许下载，部署排查用。</summary>
    private static readonly string LogFileFullPath =
        Path.GetFullPath(Path.Combine(AppConfig.BaseDir, "logs", "app.log"));

    /// <summary>跨机台 XML 落盘目录（{BaseDir}/data/agg_xml）：聚合端收到的随事件上送 XML，
    /// 与 resultsRoot 一并列入白名单放行（下载走本地文件，不依赖机台路径）。</summary>
    private static readonly string AggXmlRoot =
        Path.GetFullPath(Path.Combine(AppConfig.BaseDir, "data", "agg_xml"));

    private readonly int _port;
    private readonly AggWatcher _watcher;
    private readonly AggDatabase _db;
    private readonly string _resultsRoot;
    private readonly string _shareRoot;
    private string _token;   // 共享 token（agg_token）；空串 = 不启用鉴权。设置页可热更新

    private readonly object _lock = new();
    private HttpListener? _listener;
    private Thread? _acceptThread;
    private long _receivedCount;
    private DateTime _startedAt;   // 服务启动时间（看板页脚健康展示，v3.4.11）

    /// <summary>构造：port=监听端口，watcher=聚合监听器（推接入库/机台状态），db=聚合库（明细查询），
    /// resultsRoot=XML 报告白名单根目录（空串=拒绝全部文件下载），shareRoot=共享目录（看板页展示用）。
    /// token=共享访问令牌（agg_token，空串=不启用鉴权；配置后浏览器访问需带 ?token= 或 X-Agg-Token 头）。</summary>
    public WebAggServer(int port, AggWatcher watcher, AggDatabase db, string resultsRoot, string shareRoot, string token = "")
    {
        _port = port;
        _watcher = watcher;
        _db = db;
        _resultsRoot = resultsRoot ?? "";
        _shareRoot = shareRoot ?? "";
        _token = token ?? "";
        if (_token.Length > 0) Logger.Info($"[Web 服务] 已启用 agg_token 鉴权（看板需 ?token= 访问，推送需 X-Agg-Token 头）");
    }

    /// <summary>HttpListener 是否成功开启（Start 失败时 false，CLI 据此打印警告）。</summary>
    public bool Listening
    {
        get { lock (_lock) return _listener != null && _listener.IsListening; }
    }

    /// <summary>收到的有效请求数（路由命中且方法正确的请求才计数，404/405 不计）。</summary>
    public long ReceivedCount => Interlocked.Read(ref _receivedCount);

    /// <summary>设置被修改且已落盘时触发（AggCenterForm/Program 订阅后重启 HttpIngest/AggAlertService 热生效）。</summary>
    public event Action? SettingsChanged;

    /// <summary>启动监听：后台线程 accept；失败只记 ERROR 日志不抛异常。
    /// 前缀三档回退：http://+:port/（全网卡）→ http://0.0.0.0:port/ → http://127.0.0.1:port/
    /// （无 URL ACL 权限时的本机兜底）。</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_listener != null) return;
            var attempts = new[]
            {
                $"http://+:{_port}/",      // 全网卡（含回环），浏览器可经机台 IP 访问
                $"http://0.0.0.0:{_port}/", // Linux managed 实现下的全网卡等价写法
                $"http://127.0.0.1:{_port}/", // 无监听权限时的本机兜底
            };
            foreach (var prefix in attempts)
            {
                try
                {
                    var l = new HttpListener();
                    l.Prefixes.Add(prefix);
                    l.Start();
                    _listener = l;
                    _startedAt = DateTime.Now;
                    _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "web-agg" };
                    _acceptThread.Start();
                    Logger.Info($"[Web 服务] 已启动，监听 {prefix}");
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[Web 服务] 前缀 '{prefix}' 启动失败: {ex.Message}");
                }
            }
            _listener = null;
            Logger.Error($"[Web 服务] 启动失败（端口 {_port} 被占用或缺少监听权限）");
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

            // 鉴权（agg_token 配置后生效）：POST 推送校验 X-Agg-Token 头；
            // GET 看板/API 校验头或 ?token= 查询参数（浏览器场景）。不匹配一律 403。
            if (_token.Length > 0 && !Authorized(ctx))
            {
                Logger.Warning($"[Web 服务] 403: agg_token 缺失或不匹配（{ctx.Request.RemoteEndPoint} {ctx.Request.HttpMethod} {ctx.Request.Url?.AbsolutePath}）");
                resp.StatusCode = 403;
                return;
            }

            var method = ctx.Request.HttpMethod.ToUpperInvariant();
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            if (path.Length > 1) path = path.TrimEnd('/');   // 统一去掉尾斜杠（/ 除外）
            if (path.Length == 0) path = "/";

            // 路由：先定路径再查方法；未知路径 404，方法不符 405
            switch (path)
            {
                case "/":
                    if (method == "GET") { CountHit(); ServeDashboard(ctx); }
                    else if (method == "POST") { CountHit(); ServePush(ctx, null); }
                    else resp.StatusCode = 405;
                    break;

                case "/api/health":
                    if (method == "GET") { CountHit(); ServeHealth(ctx); }
                    else resp.StatusCode = 405;
                    break;

                case "/api/settings":
                    if (method == "GET") { CountHit(); ServeSettings(ctx); }
                    else if (method == "POST") { CountHit(); ServeSettingsSave(ctx); }
                    else resp.StatusCode = 405;
                    break;

                case "/api/machines":
                    if (method == "GET") { CountHit(); ServeMachines(ctx); }
                    else resp.StatusCode = 405;
                    break;

                case "/api/fails":
                    if (method == "GET") { CountHit(); ServeFails(ctx); }
                    else resp.StatusCode = 405;
                    break;

                case "/api/fails/count":
                    if (method == "GET") { CountHit(); ServeFailsCount(ctx); }
                    else resp.StatusCode = 405;
                    break;

                case "/api/file":
                    if (method == "GET") { CountHit(); ServeFile(ctx); }
                    else resp.StatusCode = 405;
                    break;

                case "/api/list":
                    if (method == "GET") { CountHit(); ServeList(ctx); }
                    else resp.StatusCode = 405;
                    break;

                case "/api/export.csv":
                    if (method == "GET") { CountHit(); ServeCsv(ctx); }
                    else resp.StatusCode = 405;
                    break;

                case "/api/fail":
                    if (method == "POST") { CountHit(); ServePush(ctx, "fail"); }
                    else resp.StatusCode = 405;
                    break;

                case "/api/heartbeat":
                    if (method == "POST") { CountHit(); ServePush(ctx, "heartbeat"); }
                    else resp.StatusCode = 405;
                    break;

                default:
                    resp.StatusCode = 404;
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 请求处理异常: {ex.Message}");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    private void CountHit() => Interlocked.Increment(ref _receivedCount);

    /// <summary>鉴权检查：header X-Agg-Token 或 query token 任一匹配即通过（聚合端未配置 token 时永不调用本方法）。</summary>
    private bool Authorized(HttpListenerContext ctx)
    {
        var got = ctx.Request.Headers[HttpIngest.TokenHeader];
        if (string.IsNullOrEmpty(got)) got = ctx.Request.QueryString["token"] ?? "";
        return !string.IsNullOrEmpty(got) && string.Equals(got, _token, StringComparison.Ordinal);
    }

    // ==================== GET 路由 ====================

    /// <summary>看板首页：内嵌 HTML（const 字符串，占位符替换共享目录/聚合库路径展示用）。</summary>
    private void ServeDashboard(HttpListenerContext ctx)
    {
        var page = DashboardHtml
            .Replace("%%SHARE_ROOT%%", WebUtility.HtmlEncode(_shareRoot))
            .Replace("%%DB_PATH%%", WebUtility.HtmlEncode(_db.DbPath))
            .Replace("%%RESULTS_ROOT%%", WebUtility.HtmlEncode(_resultsRoot))
            // JS 字符串上下文：反斜杠/引号必须转义，否则 "C:\Results" 里的 \R 会被 JS 当转义符吃掉
            .Replace("%%RESULTS_ROOT_JS%%", (_resultsRoot ?? "").Replace("\\", "\\\\").Replace("\"", "\\\""));
        Respond(ctx, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(page));
    }

    private void ServeHealth(HttpListenerContext ctx)
    {
        // 服务健康：部署排查 + 看板页脚展示（v3.4.11 连通性加固）
        var uptime = _startedAt == default ? 0 : (long)(DateTime.Now - _startedAt).TotalSeconds;
        var payload = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["uptime_sec"] = uptime,
            ["received"] = ReceivedCount,
        };
        Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
    }

    /// <summary>设置读取：返回聚合端当前配置（管理后台「设置」页填充用）。
    /// 只读聚合相关字段；密码类字段（token）**不回显**，只给布尔「是否已配置」。</summary>
    private void ServeSettings(HttpListenerContext ctx)
    {
        try
        {
            var cfg = AppConfig.Instance;
            var payload = new Dictionary<string, object?>
            {
                ["agg_http_port"] = cfg.AggHttpPort,
                ["agg_token_set"] = cfg.AggToken.Length > 0,
                ["agg_token"] = "",                        // 不回显，避免明文泄漏；留空表示不修改
                ["agg_webhook_set"] = cfg.AggWebhookUrl.Length > 0,
                ["agg_webhook_url"] = cfg.AggWebhookUrl,   // webhook 是飞书群的地址，非密钥，回显便于改
                ["agg_summary_minutes"] = cfg.AggSummaryMinutes,
                ["agg_share_root"] = cfg.AggShareRoot,
                ["agg_transport"] = cfg.AggTransport,
                ["results_root"] = cfg.ResultsRoot,
            };
            Respond(ctx, 200, "application/json; charset=utf-8",
                JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 读取设置失败: {ex.Message}");
            RespondText(ctx, 500, "读取设置失败");
        }
    }

    /// <summary>设置保存：更新 config.json（原子写）+ 热生效（token 立即更新）。
    /// 端口/transport/结果目录这类需要重建监听的字段，返回提示「重启聚合服务生效」。
    /// 返回 JSON：{"ok":true,"restart":bool,"msg":"..."}。</summary>
    private void ServeSettingsSave(HttpListenerContext ctx)
    {
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException) { ctx.Response.StatusCode = 413; return; }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var cfg = AppConfig.Instance;
            var changed = new List<string>();

            if (root.TryGetProperty("agg_http_port", out var pp) && pp.ValueKind == JsonValueKind.Number
                && pp.TryGetInt32(out var port) && port >= 1 && port <= 65535 && port != cfg.AggHttpPort)
            {
                cfg.AggHttpPort = port;
                changed.Add("agg_http_port");
            }
            if (root.TryGetProperty("agg_token", out var pt) && pt.ValueKind == JsonValueKind.String
                && !string.Equals(pt.GetString(), cfg.AggToken, StringComparison.Ordinal))
            {
                cfg.AggToken = pt.GetString() ?? "";
                changed.Add("agg_token");
            }
            if (root.TryGetProperty("agg_webhook_url", out var pw) && pw.ValueKind == JsonValueKind.String
                && !string.Equals(pw.GetString() ?? "", cfg.AggWebhookUrl, StringComparison.Ordinal))
            {
                cfg.AggWebhookUrl = pw.GetString() ?? "";
                changed.Add("agg_webhook_url");
            }
            if (root.TryGetProperty("agg_summary_minutes", out var pm) && pm.ValueKind == JsonValueKind.Number
                && pm.TryGetInt32(out var m) && m >= 1 && m != cfg.AggSummaryMinutes)
            {
                cfg.AggSummaryMinutes = m;
                changed.Add("agg_summary_minutes");
            }
            if (root.TryGetProperty("agg_transport", out var pt2) && pt2.ValueKind == JsonValueKind.String
                && !string.Equals(pt2.GetString() ?? "", cfg.AggTransport, StringComparison.OrdinalIgnoreCase))
            {
                var t = (pt2.GetString() ?? "").Trim().ToLowerInvariant();
                if (t == "http" || t == "smb") { cfg.AggTransport = t; changed.Add("agg_transport"); }
            }
            if (root.TryGetProperty("agg_share_root", out var ps) && ps.ValueKind == JsonValueKind.String
                && !string.Equals(ps.GetString() ?? "", cfg.AggShareRoot, StringComparison.Ordinal))
            {
                cfg.AggShareRoot = ps.GetString() ?? "";
                changed.Add("agg_share_root");
            }

            if (changed.Count == 0)
            {
                Respond(ctx, 200, "application/json; charset=utf-8",
                    Encoding.UTF8.GetBytes("{\"ok\":true,\"restart\":false,\"msg\":\"无变化\"}"));
                return;
            }

            if (!cfg.Save())
            {
                RespondText(ctx, 500, "配置保存失败（看日志）");
                return;
            }

            // 热生效：token 立即更新（本服务鉴权）；其余字段通知订阅方（AggCenterForm/Program 重启相关组件）
            bool needRestart = changed.Contains("agg_http_port") || changed.Contains("agg_transport");
            if (changed.Contains("agg_token")) _token = cfg.AggToken;
            try { SettingsChanged?.Invoke(); } catch (Exception ex) { Logger.Warning($"[Web 服务] 设置变更回调异常: {ex.Message}"); }

            var msg = needRestart
                ? "已保存。端口/传输通道变更需要重启聚合服务才生效（或重新一键部署）。"
                : "已保存并生效。";
            var respJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["restart"] = needRestart,
                ["msg"] = msg,
            });
            Respond(ctx, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(respJson));
        }
        catch (JsonException ex)
        {
            Logger.Warning($"[Web 服务] 设置 JSON 非法: {ex.Message}");
            ctx.Response.StatusCode = 400;
        }
    }

    /// <summary>机台状态列表：心跳信息来自 watcher 缓存，FAIL 统计现查库。</summary>
    private void ServeMachines(HttpListenerContext ctx)
    {
        try
        {
            var list = _watcher.GetMachines();
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(list, JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 查询机台状态失败: {ex.Message}");
            RespondText(ctx, 500, "查询机台状态失败");
        }
    }

    /// <summary>FAIL 明细 JSON：limit 默认 200 上限 1000；machine 可选（空=全部机台）；
    /// offset 分页；keyword 对 SN/型号/失败原因模糊搜索。</summary>
    private void ServeFails(HttpListenerContext ctx)
    {
        try
        {
            var q = ctx.Request.QueryString;
            var (limit, machine) = ReadLimitMachine(ctx);
            var offset = 0;
            if (int.TryParse(q["offset"], out var o) && o > 0) offset = o;
            var keyword = (q["q"] ?? "").Trim();
            var list = _db.QueryFails(limit, machine, offset, keyword.Length == 0 ? null : keyword);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(list, JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 查询 FAIL 明细失败: {ex.Message}");
            RespondText(ctx, 500, "查询 FAIL 明细失败");
        }
    }

    /// <summary>FAIL 总数 JSON（分页用）：可选 machine/q 过滤，与 /api/fails 同条件。</summary>
    private void ServeFailsCount(HttpListenerContext ctx)
    {
        try
        {
            var q = ctx.Request.QueryString;
            var machine = (q["machine"] ?? "").Trim();
            var keyword = (q["q"] ?? "").Trim();
            var count = _db.FailCount(machine.Length == 0 ? "" : machine, keyword.Length == 0 ? null : keyword);
            Respond(ctx, 200, "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes($"{{\"count\":{count}}}"));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 查询 FAIL 总数失败: {ex.Message}");
            RespondText(ctx, 500, "查询 FAIL 总数失败");
        }
    }

    /// <summary>FAIL 明细 CSV 导出：UTF-8 BOM（Excel 打开不乱码）+ 表头固定 8 列 + 公式注入防护。</summary>
    private void ServeCsv(HttpListenerContext ctx)
    {
        try
        {
            var (limit, machine) = ReadLimitMachine(ctx);
            var rows = _db.QueryFails(limit, machine);
            var sb = new StringBuilder();
            sb.AppendLine("时间,机台,型号,SN,测试日期,失败原因,测试员,结果");
            foreach (var r in rows)
            {
                sb.Append(string.Join(",", new[]
                {
                    Esc(string.IsNullOrEmpty(r.Ts) ? r.IngestTs : r.Ts),   // 时间：机台推送时间，缺省回落入库时间
                    Esc(r.Machine), Esc(r.Model), Esc(r.Sn), Esc(r.TestDate),
                    Esc(r.FailReason), Esc(r.Tester), Esc(r.Result),
                }));
                sb.AppendLine();
            }
            ctx.Response.Headers["Content-Disposition"] =
                $"attachment; filename=\"fails_{DateTime.Now:yyyyMMdd_HHmmss}.csv\"";
            // UTF-8 BOM：Encoding.GetBytes 不含 preamble，必须手动拼 EF BB BF（Excel 识别编码用）
            var enc = new UTF8Encoding(true);
            var preamble = enc.GetPreamble();
            var content = enc.GetBytes(sb.ToString());
            var bytes = new byte[preamble.Length + content.Length];
            preamble.CopyTo(bytes, 0);
            content.CopyTo(bytes, preamble.Length);
            Respond(ctx, 200, "text/csv; charset=utf-8", bytes);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 导出 CSV 失败: {ex.Message}");
            RespondText(ctx, 500, "导出 CSV 失败");
        }
    }

    /// <summary>XML 报告下载：?id= 查库取 xml_path 与 ?path= 直接传参两条入口共用白名单校验。</summary>
    private void ServeFile(HttpListenerContext ctx)
    {
        var q = ctx.Request.QueryString;
        var idStr = q["id"];
        var pathParam = q["path"];

        string target;
        if (!string.IsNullOrEmpty(idStr))
        {
            if (!long.TryParse(idStr, out var id))
            {
                RespondText(ctx, 400, "参数错误：id 必须是数字");
                return;
            }
            AggFailRow? row;
            try { row = _db.GetFailById(id); }
            catch (Exception ex)
            {
                Logger.Warning($"[Web 服务] 查询记录 id={id} 失败: {ex.Message}");
                RespondText(ctx, 500, "查询记录失败");
                return;
            }
            if (row == null || string.IsNullOrEmpty(row.XmlPath))
            {
                RespondText(ctx, 404, "记录不存在或未关联报告文件");
                return;
            }
            target = row.XmlPath;
        }
        else if (!string.IsNullOrEmpty(pathParam))
        {
            target = pathParam;
        }
        else
        {
            RespondText(ctx, 400, "参数错误：需要 id 或 path");
            return;
        }

        var status = ResolveFile(target, out var full);
        if (status == 400) { RespondText(ctx, 400, "参数错误：文件路径非法"); return; }
        if (status == 403) { RespondText(ctx, 403, ForbiddenText); return; }

        byte[] data;
        try { data = File.ReadAllBytes(full!); }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 读取报告文件失败 {full}: {ex.Message}");
            RespondText(ctx, 500, "读取文件失败");
            return;
        }

        // Content-Disposition inline + RFC 5987 UTF-8 文件名（中文文件名经浏览器正确解码）
        ctx.Response.Headers["Content-Disposition"] =
            $"inline; filename*=UTF-8''{Uri.EscapeDataString(Path.GetFileName(full!))}";
        Respond(ctx, 200, ContentTypeFor(full!), data);
    }

    /// <summary>
    /// 目录浏览：?path= 为空列出 resultsRoot 本身；否则列出该目录（相对路径按 resultsRoot 解析）。
    /// 白名单规则见 <see cref="ResolveDir"/>；只列直接子项（不递归），目录在前文件在后，
    /// 各自按名称排序（StringComparer.OrdinalIgnoreCase）。每项带完整 Path，
    /// 前端可直接回传 /api/list（进目录）或 /api/file（下载）。
    /// </summary>
    private void ServeList(HttpListenerContext ctx)
    {
        var pathParam = ctx.Request.QueryString["path"] ?? "";
        var target = string.IsNullOrEmpty(pathParam) ? _resultsRoot : pathParam;

        var status = ResolveDir(target, out var full);
        if (status == 400) { RespondText(ctx, 400, "参数错误：路径非法"); return; }
        if (status == 403) { RespondText(ctx, 403, ForbiddenDirText); return; }
        if (status == 404) { RespondText(ctx, 404, "目录不存在"); return; }

        List<ListEntry> entries;
        try
        {
            entries = new List<ListEntry>();
            // 目录 + 文件分开枚举各自排序，保证「目录在前文件在后」的语义
            var dirs = Directory.EnumerateDirectories(full!)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
            var files = Directory.EnumerateFiles(full!)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs)
            {
                var di = new DirectoryInfo(d);
                entries.Add(new ListEntry
                {
                    Name = di.Name,
                    IsDir = true,
                    Size = 0,                                        // 目录大小不给（免去递归统计）
                    Modified = di.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    Path = di.FullName,
                });
            }
            foreach (var f in files)
            {
                var fi = new FileInfo(f);
                entries.Add(new ListEntry
                {
                    Name = fi.Name,
                    IsDir = false,
                    Size = fi.Length,
                    Modified = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    Path = fi.FullName,
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 读取目录失败 {full}: {ex.Message}");
            RespondText(ctx, 500, "读取目录失败");
            return;
        }

        Respond(ctx, 200, "application/json; charset=utf-8",
            JsonSerializer.SerializeToUtf8Bytes(entries, JsonOpts));
    }

    /// <summary>目录浏览列表项（/api/list 用）：public 属性，默认序列化即可（IncludeFields 不影响属性）。</summary>
    private sealed class ListEntry
    {
        public string Name { get; set; } = "";
        public bool IsDir { get; set; }
        public long Size { get; set; }
        public string Modified { get; set; } = "";
        public string Path { get; set; } = "";
    }

    /// <summary>limit/machine 参数解析：limit 默认 200，非法/<=0 回落默认，>1000 截断；machine 空串转 null。</summary>
    private static (int limit, string? machine) ReadLimitMachine(HttpListenerContext ctx)
    {
        var q = ctx.Request.QueryString;
        var limit = DefaultLimit;
        if (int.TryParse(q["limit"], out var l) && l > 0) limit = Math.Min(l, MaxLimit);
        var machine = (q["machine"] ?? "").Trim();
        return (limit, machine.Length == 0 ? null : machine);
    }

    // ==================== POST 路由（机台推送） ====================

    /// <summary>机台推送：显式路径（forcedType 非空）或根路径按 body type 分发。
    /// 一律回 200（回调异常也 200），协议错误回 4xx。</summary>
    private void ServePush(HttpListenerContext ctx, string? forcedType)
    {
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException)
        {
            ctx.Response.StatusCode = 413;   // body 超 1MB
            return;
        }

        string? type;
        try
        {
            using var doc = JsonDocument.Parse(body);
            type = forcedType;
            if (type == null)
                type = doc.RootElement.TryGetProperty("type", out var tv) ? tv.GetString() : null;
        }
        catch (JsonException ex)
        {
            Logger.Warning($"[Web 服务] body 不是合法 JSON: {ex.Message}");
            ctx.Response.StatusCode = 400;
            return;
        }

        if (type == null)
        {
            ctx.Response.StatusCode = 400;   // 根路径但缺 type = 机台发错内容
            return;
        }

        try
        {
            if (string.Equals(type, "fail", StringComparison.OrdinalIgnoreCase)) _watcher.IngestFail(body);
            else if (string.Equals(type, "heartbeat", StringComparison.OrdinalIgnoreCase)) _watcher.IngestHeartbeat(body);
            else
            {
                ctx.Response.StatusCode = 400;   // type 不是 fail/heartbeat
                return;
            }
        }
        catch (AggIngestException ex)
        {
            // 数据库写入失败：回 500 —— 机台端断线补偿队列收到非 2xx 自动重推
            Logger.Error($"[Web 服务] fail 入库失败 machine={ex.Machine} seq={ex.Seq}: {ex.Message}");
            ctx.Response.StatusCode = 500;
            return;
        }
        catch (Exception ex)
        {
            // 聚合端逻辑错误绝不回 4xx/5xx：机台端不重推，重复风险交给 (machine, seq) 幂等兜底
            Logger.Warning($"[Web 服务] 推送回调异常（仍回 200）: {ex.Message}");
        }
        ctx.Response.StatusCode = 200;
    }

    // ==================== 文件白名单（安全重点） ====================

    /// <summary>
    /// 文件白名单校验（?id= 查出的 xml_path 与 ?path= 直接传参共用）。
    /// 规则：
    ///   1. Path.GetFullPath 归一化（Windows 大小写不敏感、分隔符统一）；
    ///   2. 必须位于 resultsRoot 内（root 加尾分隔符后 StartsWith 比较，防 /root2 前缀误伤）；
    ///   3. 文件必须真实存在；
    ///   4. 例外白名单：{BaseDir}/logs/app.log 始终放行（resultsRoot 为空时唯一可用）。
    /// 返回：200=放行（out 完整路径），400=路径非法，403=不在白名单或文件不存在。</summary>
    private int ResolveFile(string path, out string? full)
    {
        full = null;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 路径非法 '{path}': {ex.Message}");
            return 400;
        }

        // 日志白名单：聚合服务日志始终可下载（部署排查用）
        if (string.Equals(full, LogFileFullPath, StringComparison.OrdinalIgnoreCase))
            return File.Exists(full) ? 200 : 403;

        // 跨机台 XML 目录放行（data/agg_xml/{machine}/{seq}.xml）
        if (full.StartsWith(AggXmlRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return File.Exists(full) ? 200 : 403;

        if (string.IsNullOrEmpty(_resultsRoot)) return 403;   // 未配置白名单根：拒绝全部

        string root;
        try { root = Path.GetFullPath(_resultsRoot); }
        catch { return 400; }
        var rootSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootSep, StringComparison.OrdinalIgnoreCase)) return 403;
        if (!File.Exists(full)) return 403;
        return 200;
    }

    /// <summary>
    /// 目录白名单校验（/api/list 用）。与 <see cref="ResolveFile"/> 同思路，区别：
    ///   1. 相对路径先按 resultsRoot 解析（/api/list?path=sub 等价于 resultsRoot\sub）；
    ///   2. 根目录本身放行（path 为空或指向 resultsRoot 自己）；
    ///   3. 目标是**目录**才算数：不存在/是文件 → 404（前端据此提示）。
    /// 返回：200=放行（out 完整目录路径），400=路径非法，403=不在白名单，404=目录不存在。</summary>
    private int ResolveDir(string path, out string? full)
    {
        full = null;
        // 相对路径（如 "20260812"、"sub/dir"）一律按 resultsRoot 解析，不依赖服务进程工作目录
        string target;
        try { target = !string.IsNullOrEmpty(path) && !Path.IsPathRooted(path) ? Path.Combine(_resultsRoot, path) : path; }
        catch { return 400; }
        try { full = Path.GetFullPath(target); }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 路径非法 '{path}': {ex.Message}");
            return 400;
        }

        if (string.IsNullOrEmpty(_resultsRoot)) return 403;   // 未配置白名单根：拒绝全部

        string root;
        try { root = Path.GetFullPath(_resultsRoot); }
        catch { return 400; }
        // 根目录本身放行（path 为空时 full == root），其余必须位于根下
        if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            var rootSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootSep, StringComparison.OrdinalIgnoreCase)) return 403;
        }
        if (!Directory.Exists(full)) return 404;
        return 200;
    }

    /// <summary>按扩展名给 Content-Type：.xml→application/xml；文本类→text/plain；其余八进制流。</summary>
    private static string ContentTypeFor(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".xml" => "application/xml",
            ".txt" or ".log" or ".csv" or ".ini" or ".json" => "text/plain; charset=utf-8",
            _ => "application/octet-stream",
        };
    }

    /// <summary>
    /// CSV 字段转义。⚠ CWE-1236 公式注入防护：Excel/WPS 会把 = + - @ 开头（或 \t \r 前导）
    /// 的单元格当公式执行，FAIL 明细字段来自机台 XML/JSON 自由文本，首字符命中时加单引号前缀；
    /// 含逗号/引号/换行的字段双引号包裹、内部双引号翻倍。
    /// </summary>
    private static string Esc(string? v)
    {
        v ??= "";
        if (v.Length > 0 && (v[0] == '=' || v[0] == '+' || v[0] == '-' || v[0] == '@' ||
                             v[0] == '\t' || v[0] == '\r'))
            v = "'" + v;
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r'))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }

    // ==================== 通用 ====================

    private static void Respond(HttpListenerContext ctx, int status, string contentType, byte[] body)
    {
        var resp = ctx.Response;
        resp.StatusCode = status;
        resp.ContentType = contentType;
        resp.ContentLength64 = body.Length;
        if (body.Length > 0) resp.OutputStream.Write(body, 0, body.Length);
    }

    private static void RespondText(HttpListenerContext ctx, int status, string text)
        => Respond(ctx, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

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

    // ==================== 内嵌看板页 ====================

    /// <summary>
    /// 看板首页 HTML：内嵌常量，无任何外部资源（内联 CSS+JS），可直接用浏览器打开。
    /// %%SHARE_ROOT%% / %%DB_PATH%% / %%RESULTS_ROOT%%（HTML 展示）/ %%RESULTS_ROOT_JS%%（JS 字符串）
    /// 四个占位符在响应时替换（转义后展示）。
    /// 顶部「看板 | 文件」两个页签 JS 切换：看板页 fetch /api/machines + /api/fails 渲染，
    /// 3s 自动刷新（可关）；文件页 fetch /api/list 渲染目录列表（面包屑导航、目录点击进入、
    /// 文件点击新窗口下载）。全部动态文本经 esc() 转义防 XSS。
    /// </summary>
    private const string DashboardHtml = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Argus 多机台聚合看板</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body { background: #171a21; color: #d8dee9; font-family: "Microsoft YaHei", "PingFang SC", "Segoe UI", sans-serif; padding: 16px; }
  header { display: flex; align-items: center; gap: 14px; flex-wrap: wrap; margin-bottom: 16px; }
  h1 { font-size: 20px; color: #ffffff; }
  .stat { color: #e5534b; font-weight: bold; }
  button { background: #2f3542; color: #ffffff; border: 1px solid #454c5c; border-radius: 4px; padding: 6px 14px; cursor: pointer; font-size: 13px; }
  button:hover { background: #3a4152; }
  label { color: #9aa3b2; font-size: 13px; cursor: pointer; }
  .tabs { display: flex; gap: 8px; margin-bottom: 16px; }
  .tab.active { background: #3a4152; border-color: #4c9aff; }
  .files-bar { color: #9aa3b2; font-size: 13px; margin-bottom: 10px; }
  #fileRoot { color: #4c9aff; font-family: Consolas, "Courier New", monospace; }
  .crumb { margin-bottom: 10px; font-size: 13px; }
  .crumb a { color: #4c9aff; text-decoration: none; margin-right: 6px; }
  .crumb a:hover { text-decoration: underline; }
  .crumb .sep { color: #5c6470; margin-right: 6px; }
  td.fname.dir { color: #4c9aff; cursor: pointer; font-weight: bold; }
  td.fname.file { cursor: pointer; }
  #fileMsg { color: #e5534b; font-size: 13px; margin-top: 10px; }
  .cards { display: flex; flex-wrap: wrap; gap: 12px; margin-bottom: 20px; }
  .card { background: #20242d; border: 1px solid #2f3542; border-left: 4px solid #3fb950; border-radius: 6px; padding: 10px 14px; min-width: 178px; font-size: 13px; line-height: 1.7; }
  .card.offline { border-left-color: #e5534b; opacity: .85; }
  .card .mname { font-size: 15px; font-weight: bold; color: #ffffff; }
  .dot { font-size: 12px; }
  .dot.on { color: #3fb950; }
  .dot.off { color: #e5534b; }
  .fail { color: #e5534b; font-weight: bold; }
  table { width: 100%; border-collapse: collapse; background: #20242d; font-size: 13px; }
  th, td { border: 1px solid #2f3542; padding: 6px 8px; text-align: left; white-space: nowrap; }
  th { background: #2a2f3a; color: #9aa3b2; font-weight: normal; }
  tr:hover td { background: #262b36; }
  td.reason { max-width: 320px; overflow: hidden; text-overflow: ellipsis; }
  .empty { color: #5c6470; text-align: center; padding: 24px !important; white-space: normal !important; }
  a.xml { color: #4c9aff; text-decoration: none; }
  a.xml:hover { text-decoration: underline; }
  #exportLink { display: inline-block; margin-top: 14px; color: #4c9aff; text-decoration: none; font-size: 13px; }
  #exportLink:hover { text-decoration: underline; }
  .filters { display: flex; gap: 8px; margin-bottom: 10px; align-items: center; }
  .filters input { background: #20242d; border: 1px solid #2f3542; color: #d8dee9; border-radius: 4px; padding: 6px 10px; font-size: 13px; flex: 1; max-width: 360px; }
  .filters input:focus { outline: none; border-color: #4c9aff; }
  .pager { display: flex; gap: 10px; margin-top: 12px; align-items: center; color: #9aa3b2; font-size: 13px; }
  .pager button:disabled { opacity: .4; cursor: default; }
  #settingsView h3 { color: #ffffff; font-size: 15px; margin-bottom: 12px; }
  #settingsView .set-row { display: flex; align-items: center; gap: 10px; margin-bottom: 10px; }
  #settingsView .set-row label { width: 170px; color: #9aa3b2; font-size: 13px; flex-shrink: 0; }
  #settingsView .set-row input, #settingsView .set-row select {
    background: #20242d; border: 1px solid #2f3542; color: #d8dee9; border-radius: 4px;
    padding: 6px 10px; font-size: 13px; width: 360px;
  }
  #settingsView .set-row input:focus, #settingsView .set-row select:focus { outline: none; border-color: #4c9aff; }
  #settingsView .set-row .hint { color: #5c6470; font-size: 12px; }
  #settingsView .set-actions { margin-top: 16px; display: flex; align-items: center; gap: 12px; }
  #setMsg { color: #4c9aff; font-size: 13px; }
  #setMsg.err { color: #e5534b; }
  footer { margin-top: 18px; color: #5c6470; font-size: 12px; line-height: 1.8; }
</style>
</head>
<body>
<header>
  <h1>Argus 多机台聚合看板</h1>
  <span>累计 FAIL：<span class="stat" id="totalFails">0</span></span>
  <button id="btnRefresh">刷新</button>
  <label><input type="checkbox" id="autoRefresh" checked> 自动刷新(3秒)</label>
</header>

<nav class="tabs">
  <button class="tab active" id="tabDash">看板</button>
  <button class="tab" id="tabFiles">文件</button>
  <button class="tab" id="tabSettings">设置</button>
</nav>

<div id="dashView">
<div class="cards" id="cards"><div class="empty">加载中...</div></div>

<div class="filters">
  <input type="text" id="searchBox" placeholder="搜索 SN / 型号 / 失败原因..." value="">
  <button id="btnSearch">搜索</button>
  <button id="btnClearSearch">清空</button>
</div>

<table>
  <thead>
    <tr><th>时间</th><th>机台</th><th>型号</th><th>SN</th><th>测试日期</th><th>失败原因</th><th>测试员</th><th>结果</th><th>报告</th></tr>
  </thead>
  <tbody id="rows"><tr><td colspan="9" class="empty">暂无数据</td></tr></tbody>
</table>

<div class="pager">
  <button id="btnPrev">上一页</button>
  <span id="pageInfo">第 1 页</span>
  <button id="btnNext">下一页</button>
  <span id="totalInfo"></span>
</div>

<a id="exportLink" href="/api/export.csv">导出 CSV</a></div>

<div id="filesView" style="display:none">
  <div class="files-bar">浏览根目录：<span id="fileRoot"></span></div>
  <div class="crumb" id="crumb"></div>
  <table>
    <thead>
      <tr><th>名称</th><th>类型/大小</th><th>修改时间</th><th>操作</th></tr>
    </thead>
    <tbody id="fileRows"><tr><td colspan="4" class="empty">加载中...</td></tr></tbody>
  </table>
  <div id="fileMsg"></div>
</div>

<div id="settingsView" style="display:none">
  <h3>聚合服务设置</h3>
  <div class="set-row">
    <label>监听端口</label>
    <input type="number" id="setPort" min="1" max="65535" value="8080">
    <span class="hint">改后需重启聚合服务生效</span>
  </div>
  <div class="set-row">
    <label>访问令牌（agg_token）</label>
    <input type="password" id="setToken" placeholder="留空=不修改；填入新值立即生效">
    <span class="hint" id="tokenState"></span>
  </div>
  <div class="set-row">
    <label>飞书告警 webhook</label>
    <input type="text" id="setWebhook" placeholder="https://open.feishu.cn/open-apis/...">
    <span class="hint">机台离线告警 + 定时汇总推送目标；留空关闭</span>
  </div>
  <div class="set-row">
    <label>汇总推送间隔（分钟）</label>
    <input type="number" id="setSummary" min="1" max="1440" value="60">
    <span class="hint">每 N 分钟向飞书推一次各机台汇总</span>
  </div>
  <div class="set-row">
    <label>传输通道</label>
    <select id="setTransport">
      <option value="http">http（机台直推）</option>
      <option value="smb">smb（共享目录）</option>
    </select>
    <span class="hint">改后需重启聚合服务生效</span>
  </div>
  <div class="set-row">
    <label>共享目录根</label>
    <input type="text" id="setShareRoot" placeholder="D:\ArgusAgg">
    <span class="hint">smb 通道机台目录根；http 通道可留空</span>
  </div>
  <div class="set-row">
    <label>报告白名单根（results_root）</label>
    <input type="text" id="setResultsRoot" placeholder="D:\Results">
    <span class="hint">XML 下载/文件浏览白名单，目录外一律拒绝</span>
  </div>
  <div class="set-actions">
    <button id="btnSaveSettings">保存设置</button>
    <span id="setMsg"></span>
  </div>
</div>

<footer>
  监听共享目录：%%SHARE_ROOT%%<br>
  聚合库：%%DB_PATH%%<br>
  服务运行：<span id="svcHealth">…</span>
</footer>

<script>
// ==================== 鉴权（agg_token 配置后） ====================
// token 来源：URL ?token= 或 localStorage（刷新/翻页后保持）；所有 fetch 统一带 X-Agg-Token 头。
var AGG_TOKEN = new URLSearchParams(location.search).get("token") || localStorage.getItem("agg_token") || "";
if (AGG_TOKEN) localStorage.setItem("agg_token", AGG_TOKEN);

function apiHeaders() {
  var h = { "Content-Type": "application/json" };
  if (AGG_TOKEN) h["X-Agg-Token"] = AGG_TOKEN;
  return h;
}

async function apiFetch(url) {
  var res = await fetch(url, { headers: apiHeaders() });
  if (res.status === 403 && AGG_TOKEN) {
    // token 失效（服务端改了 token）：清掉本地缓存，提示重新带 ?token= 访问
    localStorage.removeItem("agg_token");
    AGG_TOKEN = "";
  }
  return res;
}

function esc(s) {
  return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) {
    return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
  });
}

// ==================== 搜索 / 分页 ====================
var PAGE_SIZE = 100;
var curPage = 0;         // 0 起
var curKeyword = "";
var failTotal = -1;      // -1 = 未知（还没查到）

function buildFailsUrl(offset) {
  var url = "/api/fails?limit=" + PAGE_SIZE + "&offset=" + offset;
  if (curKeyword) url += "&q=" + encodeURIComponent(curKeyword);
  return url;
}

function renderPager() {
  document.getElementById("pageInfo").textContent = "第 " + (curPage + 1) + " 页";
  document.getElementById("btnPrev").disabled = curPage <= 0;
  document.getElementById("btnNext").disabled = failTotal < 0 || (curPage + 1) * PAGE_SIZE >= failTotal;
  document.getElementById("totalInfo").textContent = failTotal >= 0 ? "共 " + failTotal + " 条" : "";
}

async function refreshFails() {
  var res = await apiFetch(buildFailsUrl(curPage * PAGE_SIZE));
  if (!res.ok) return;
  renderFails(await res.json());
  var cres = await apiFetch("/api/fails/count?limit=1" + (curKeyword ? "&q=" + encodeURIComponent(curKeyword) : ""));
  if (cres.ok) {
    var cj = await cres.json();
    if (typeof cj.count === "number") failTotal = cj.count;
  }
  renderPager();
}

async function refresh() {
  try {
    var res = await Promise.all([
      apiFetch("/api/machines"),
      refreshFails()
    ]);
    if (!res[0].ok) return;
    var machines = await res[0].json();
    renderMachines(machines);
    checkOfflineBeep(machines);
    var total = 0;
    for (var i = 0; i < machines.length; i++) total += (machines[i].FailCount || 0);
    document.getElementById("totalFails").textContent = total;
  } catch (e) { /* 服务未就绪时静默，下轮再试 */ }
}

// ==================== 服务健康（v3.4.11 连通性加固） ====================
// 页脚展示运行时长与累计接收推送数 —— 聚合服务活着没、收没收到数据，一眼可见。
async function refreshHealth() {
  try {
    var res = await apiFetch("/api/health");
    if (!res.ok) { document.getElementById("svcHealth").textContent = "服务异常"; return; }
    var h = await res.json();
    var up = h.uptime_sec || 0;
    var hh = Math.floor(up / 3600), mm = Math.floor((up % 3600) / 60), ss = up % 60;
    var upText = (hh > 0 ? hh + " 小时 " : "") + mm + " 分 " + ss + " 秒";
    document.getElementById("svcHealth").textContent =
      "已 " + upText + " · 累计接收推送 " + (h.received || 0) + " 次";
  } catch (e) { document.getElementById("svcHealth").textContent = "健康检查失败"; }
}

// ==================== 离线声音提醒 ====================
// 机台从在线 → 离线翻转时播一次短蜂鸣（Web Audio，无需外部资源）。
// 首次加载不响（prevOnline 为空表）；同一机台重复翻转靠音频节流（1 秒内不连响）。
var prevOnline = null;      // { machine: bool, ... }
var lastBeepAt = 0;

function checkOfflineBeep(machines) {
  var now = Date.now();
  if (prevOnline) {
    for (var i = 0; i < machines.length; i++) {
      var m = machines[i];
      var was = prevOnline[m.Machine];
      if (was === true && m.Online === false && now - lastBeepAt > 1000) {
        playBeep();
        lastBeepAt = now;
        break;
      }
    }
  }
  prevOnline = {};
  for (var i = 0; i < machines.length; i++) prevOnline[machines[i].Machine] = machines[i].Online;
}

function playBeep() {
  try {
    var AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return;
    var ctx = new AC();
    var osc = ctx.createOscillator();
    var gain = ctx.createGain();
    osc.type = "square";
    osc.frequency.value = 880;
    gain.gain.value = 0.15;
    osc.connect(gain).connect(ctx.destination);
    osc.start();
    osc.stop(ctx.currentTime + 0.35);
    setTimeout(function () { ctx.close(); }, 600);
  } catch (e) { /* 音频不可用就静默 */ }
}

function renderMachines(list) {
  var box = document.getElementById("cards");
  if (!list || list.length === 0) {
    box.innerHTML = '<div class="empty">暂无数据</div>';
    return;
  }
  var html = "";
  for (var i = 0; i < list.length; i++) {
    var m = list[i];
    html += '<div class="card' + (m.Online ? "" : " offline") + '">' +
      '<div class="mname">' + esc(m.Machine) +
      ' <span class="dot ' + (m.Online ? "on" : "off") + '">&#9679;</span></div>' +
      '<div>最近心跳：' + esc(m.LastHeartbeat || "-") + '</div>' +
      '<div>累计 FAIL：<span class="fail">' + (m.FailCount || 0) + '</span></div>' +
      '<div>最近 FAIL：' + esc(m.LastFailAt || "-") + '</div>' +
      '<div>队列数：' + (m.Queued || 0) + '</div>' +
      '</div>';
  }
  box.innerHTML = html;
}

function xmlHref(p) {
  return p + (AGG_TOKEN ? (p.indexOf("?") >= 0 ? "&" : "?") + "token=" + encodeURIComponent(AGG_TOKEN) : "");
}

function renderFails(list) {
  var tb = document.getElementById("rows");
  if (!list || list.length === 0) {
    tb.innerHTML = '<tr><td colspan="9" class="empty">暂无数据</td></tr>';
    return;
  }
  var html = "";
  for (var i = 0; i < list.length; i++) {
    var r = list[i];
    html += '<tr>' +
      '<td>' + esc(r.Ts || r.IngestTs || "") + '</td>' +
      '<td>' + esc(r.Machine) + '</td>' +
      '<td>' + esc(r.Model) + '</td>' +
      '<td>' + esc(r.Sn) + '</td>' +
      '<td>' + esc(r.TestDate) + '</td>' +
      '<td class="reason" title="' + esc(r.FailReason) + '">' + esc(r.FailReason) + '</td>' +
      '<td>' + esc(r.Tester) + '</td>' +
      '<td>' + esc(r.Result) + '</td>' +
      '<td><a class="xml" href="' + xmlHref('/api/file?id=' + r.Id) + '" target="_blank">XML</a></td>' +
      '</tr>';
  }
  tb.innerHTML = html;
}

document.getElementById("btnRefresh").onclick = refresh;
document.getElementById("btnSearch").onclick = function () {
  curKeyword = document.getElementById("searchBox").value.trim();
  curPage = 0;
  failTotal = -1;
  refresh();
};
document.getElementById("btnClearSearch").onclick = function () {
  document.getElementById("searchBox").value = "";
  curKeyword = "";
  curPage = 0;
  failTotal = -1;
  refresh();
};
document.getElementById("searchBox").onkeydown = function (e) {
  if (e.key === "Enter") document.getElementById("btnSearch").onclick();
};
document.getElementById("btnPrev").onclick = function () {
  if (curPage > 0) { curPage--; refresh(); }
};
document.getElementById("btnNext").onclick = function () {
  if (failTotal < 0 || (curPage + 1) * PAGE_SIZE < failTotal) { curPage++; refresh(); }
};
refresh();
setInterval(function () {
  if (document.getElementById("autoRefresh").checked) refresh();
}, 3000);
refreshHealth();
setInterval(refreshHealth, 15000);

// CSV 导出链接：带 token 时附加 ?token= 让 <a href> 直接下载也能过鉴权
(function () {
  var el = document.getElementById("exportLink");
  if (el && AGG_TOKEN) el.href = "/api/export.csv?token=" + encodeURIComponent(AGG_TOKEN);
})();

// ==================== 页签切换 ====================
function showTab(name) {
  var dash = name === "dash", files = name === "files", set = name === "settings";
  document.getElementById("tabDash").classList.toggle("active", dash);
  document.getElementById("tabFiles").classList.toggle("active", files);
  document.getElementById("tabSettings").classList.toggle("active", set);
  document.getElementById("dashView").style.display = dash ? "" : "none";
  document.getElementById("filesView").style.display = files ? "" : "none";
  document.getElementById("settingsView").style.display = set ? "" : "none";
  if (files) loadFiles();            // 进文件页才拉列表（懒加载）
  if (set) loadSettings();           // 进设置页才拉配置（懒加载）
}
document.getElementById("tabDash").onclick = function () { showTab("dash"); };
document.getElementById("tabFiles").onclick = function () { showTab("files"); };
document.getElementById("tabSettings").onclick = function () { showTab("settings"); };

// ==================== 设置页（管理后台开关） ====================
var settingsLoaded = false;

async function loadSettings() {
  var res = await apiFetch("/api/settings");
  if (!res.ok) return;
  var s = await res.json();
  document.getElementById("setPort").value = s.agg_http_port || 8080;
  document.getElementById("setWebhook").value = s.agg_webhook_url || "";
  document.getElementById("setSummary").value = s.agg_summary_minutes || 60;
  document.getElementById("setTransport").value = s.agg_transport || "http";
  document.getElementById("setShareRoot").value = s.agg_share_root || "";
  document.getElementById("setResultsRoot").value = s.results_root || "";
  document.getElementById("tokenState").textContent = s.agg_token_set ? "（已配置）" : "（未配置）";
  document.getElementById("setToken").value = "";
  settingsLoaded = true;
}

document.getElementById("btnSaveSettings").onclick = async function () {
  var msg = document.getElementById("setMsg");
  msg.textContent = "保存中...";
  msg.className = "";
  var port = parseInt(document.getElementById("setPort").value, 10);
  var summary = parseInt(document.getElementById("setSummary").value, 10);
  var body = {
    agg_http_port: isNaN(port) ? 8080 : port,
    agg_token: document.getElementById("setToken").value,
    agg_webhook_url: document.getElementById("setWebhook").value.trim(),
    agg_summary_minutes: isNaN(summary) ? 60 : summary,
    agg_transport: document.getElementById("setTransport").value,
    agg_share_root: document.getElementById("setShareRoot").value.trim(),
    results_root: document.getElementById("setResultsRoot").value.trim(),
  };
  try {
    var res = await fetch("/api/settings", {
      method: "POST",
      headers: apiHeaders(),
      body: JSON.stringify(body)
    });
    var r = await res.json();
    if (r.ok) {
      msg.textContent = r.msg || "已保存";
      document.getElementById("setToken").value = "";
      loadSettings();
    } else {
      msg.className = "err";
      msg.textContent = r.msg || "保存失败";
    }
  } catch (e) {
    msg.className = "err";
    msg.textContent = "保存失败：" + e;
  }
};

// ==================== 文件浏览（/api/list） ====================
var rootPath = "%%RESULTS_ROOT_JS%%";
document.getElementById("fileRoot").textContent = rootPath || "（未配置）";
// 当前浏览位置：相对根目录的路径（"" = 根），?path= 参数可直达（相对或绝对都行）
var current = new URLSearchParams(location.search).get("path") || "";

// 深链接：?view=files 直达文件页（?path= 直达目录），方便收藏/部署排查。
// 必须在 current 定义之后调用（showTab→loadFiles 会读 current）
var initView = new URLSearchParams(location.search).get("view") || "dash";
showTab(initView === "files" ? "files" : initView === "settings" ? "settings" : "dash");

function fmtSize(n) {
  if (n < 1024) return n + " B";
  if (n < 1024 * 1024) return (n / 1024).toFixed(1) + " KB";
  return (n / 1024 / 1024).toFixed(1) + " MB";
}

function renderCrumb() {
  var segs = current ? current.split(/[\\\/]+/).filter(function (s) { return s.length > 0; }) : [];
  var html = '<a href="javascript:void(0)" data-rel="">根目录</a>';
  var rel = "";
  for (var i = 0; i < segs.length; i++) {
    rel = rel + (rel ? "/" : "") + segs[i];
    html += '<span class="sep">/</span><a href="javascript:void(0)" data-rel="' + esc(rel) + '">' + esc(segs[i]) + '</a>';
  }
  document.getElementById("crumb").innerHTML = html;
}

async function loadFiles() {
  var tb = document.getElementById("fileRows");
  var msg = document.getElementById("fileMsg");
  tb.innerHTML = '<tr><td colspan="4" class="empty">加载中...</td></tr>';
  msg.textContent = "";
  renderCrumb();
  try {
    var url = "/api/list" + (current ? "?path=" + encodeURIComponent(current) : "");
    var res = await apiFetch(url);
    if (res.status === 403 || res.status === 404) {
      msg.textContent = await res.text();
      tb.innerHTML = '<tr><td colspan="4" class="empty">无法浏览该目录</td></tr>';
      return;
    }
    if (!res.ok) {
      msg.textContent = "加载失败（HTTP " + res.status + "）";
      tb.innerHTML = '<tr><td colspan="4" class="empty">暂无数据</td></tr>';
      return;
    }
    var list = await res.json();
    if (!list || list.length === 0) {
      tb.innerHTML = '<tr><td colspan="4" class="empty">空目录</td></tr>';
      return;
    }
    var html = "";
    for (var i = 0; i < list.length; i++) {
      var it = list[i];
      html += '<tr>' +
        '<td class="fname ' + (it.IsDir ? "dir" : "file") + '"' +
        ' data-dir="' + (it.IsDir ? "1" : "0") + '"' +
        ' data-name="' + esc(it.Name) + '" data-path="' + esc(it.Path) + '">' + esc(it.Name) + '</td>' +
        '<td>' + (it.IsDir ? "目录" : fmtSize(it.Size)) + '</td>' +
        '<td>' + esc(it.Modified) + '</td>' +
        '<td>' + (it.IsDir ? "" :
          '<a class="xml" href="' + xmlHref('/api/file?path=' + encodeURIComponent(it.Path)) + '" target="_blank">下载</a>') + '</td>' +
        '</tr>';
    }
    tb.innerHTML = html;
  } catch (e) {
    msg.textContent = "加载失败：" + e;
    tb.innerHTML = '<tr><td colspan="4" class="empty">暂无数据</td></tr>';
  }
}

// 目录行点击进入；文件行点击（或下载按钮）新窗口下载
document.getElementById("fileRows").onclick = function (ev) {
  var td = ev.target.closest ? ev.target.closest("td.fname") : null;
  if (!td) return;
  var p = td.getAttribute("data-path");
  if (td.getAttribute("data-dir") === "1") {
    current = (current ? current.replace(/[\\\/]+$/, "") + "/" : "") + td.getAttribute("data-name");
    loadFiles();
  } else {
    window.open(xmlHref("/api/file?path=" + encodeURIComponent(p)), "_blank");
  }
};

// 面包屑：点击回到任意上级目录
document.getElementById("crumb").onclick = function (ev) {
  var a = ev.target.closest ? ev.target.closest("a") : null;
  if (!a) return;
  current = a.getAttribute("data-rel") || "";
  loadFiles();
};
</script>
</body>
</html>
""";
}
