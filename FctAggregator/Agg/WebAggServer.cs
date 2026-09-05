using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace FctAggregator;

public class WebAggServer : IDisposable
{
    private const int MaxBodyBytes = 1024 * 1024;
    private const int ReadChunk = 16384;
    private const int DefaultLimit = 200;
    private const int MaxLimit = 1000;
    private const string ForbiddenText = "拒绝访问：文件不在允许目录内";
    private const string ForbiddenDirText = "拒绝访问：目录不在允许范围内";

    private static readonly JsonSerializerOptions JsonOpts = new() { IncludeFields = true };

    private static readonly string LogFileFullPath =
        Path.GetFullPath(Path.Combine(AppConfig.BaseDir, "logs", "app.log"));

    private static readonly string AggXmlRoot =
        Path.GetFullPath(Path.Combine(AppConfig.BaseDir, "data", "agg_xml"));

    private readonly int _port;
#pragma warning disable CS0618
    private readonly AggWatcher? _watcher;
#pragma warning restore CS0618
    private readonly MeshNode? _mesh;
    private readonly AggDatabase _db;
    private readonly string _resultsRoot;
    private readonly string _shareRoot;
    private string _token;

    private readonly object _lock = new();
    private HttpListener? _listener;
    private Thread? _acceptThread;
    private long _receivedCount;
    private DateTime _startedAt;

    private readonly RoutePipeline _pipeline = new();

#pragma warning disable CS0618
    public WebAggServer(int port, AggWatcher? watcher, AggDatabase db, string resultsRoot, string shareRoot, string token = "")
    {
        _port = port;
        _watcher = watcher;
        _db = db;
        _resultsRoot = resultsRoot ?? "";
        _shareRoot = shareRoot ?? "";
        _token = token ?? "";
        if (_token.Length > 0) Logger.Info($"[Web 服务] 已启用 agg_token 鉴权（看板需 ?token= 访问，推送需 X-Agg-Token 头）");
        else Logger.Warning("[Web 服务] agg_token 未配置，当前为未鉴权模式（任意客户端可访问），请在设置页配置token");
        RegisterRoutes();
    }
#pragma warning restore CS0618

    public WebAggServer(int port, MeshNode mesh, AggDatabase db, string resultsRoot, string shareRoot, string token = "")
#pragma warning disable CS0618
        : this(port, (AggWatcher?)null, db, resultsRoot, shareRoot, token)
#pragma warning restore CS0618
    {
        _mesh = mesh;
        mesh.Receiver.LocalReadValidator = path => ResolveFile(path, out _) == 200;
    }

    private void RegisterRoutes()
    {
        _pipeline.Add("GET", "/", ctx => { CountHit(); ServeDashboard(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/legacy", ctx => { CountHit(); ServeLegacyDashboard(ctx); return Task.CompletedTask; });
        _pipeline.Add("POST", "/", ctx => { CountHit(); ServePush(ctx, null); return Task.CompletedTask; });

        _pipeline.Add("GET", "/api/health", ctx => { CountHit(); ServeHealth(ctx); return Task.CompletedTask; });

        _pipeline.Add("GET", "/api/settings", ctx => { CountHit(); ServeSettings(ctx); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/settings", ctx => { CountHit(); ServeSettingsSave(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/config/validate", ctx => { CountHit(); ServeConfigValidate(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/config/recommend", ctx => { CountHit(); ServeConfigRecommend(ctx); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/config/rollback", ctx => { CountHit(); ServeConfigRollback(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/config/backups", ctx => { CountHit(); ServeConfigBackups(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/devices/predict", ctx => { CountHit(); ServeDevicesPredict(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/devices/inspect", ctx => { CountHit(); ServeDevicesInspect(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/fct/changes", ctx => { CountHit(); ServeFctChanges(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/todos/suggest", ctx => { CountHit(); ServeTodoSuggest(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/maintenance/advise", ctx => { CountHit(); ServeMaintenanceAdvise(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/highlights", ctx => { CountHit(); ServeHighlights(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/layout/suggest", ctx => { CountHit(); ServeLayoutSuggest(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/alerts/predict", ctx => { CountHit(); ServeAlertsPredict(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/alerts/heal", ctx => { CountHit(); ServeAlertsHeal(ctx); return Task.CompletedTask; });

        _pipeline.Add("GET", "/api/machines", ctx => { CountHit(); ServeMachines(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/fails", ctx => { CountHit(); ServeFails(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/fails/count", ctx => { CountHit(); ServeFailsCount(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/file", ctx => { CountHit(); ServeFile(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/list", ctx => { CountHit(); ServeList(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/export.csv", ctx => { CountHit(); ServeCsv(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/xmlview", ctx => { CountHit(); ServeXmlView(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/stats", ctx => { CountHit(); ServeStats(ctx); return Task.CompletedTask; });

        _pipeline.Add("GET", "/api/predict/accuracy", ctx => { CountHit(); ServePredictAccuracy(ctx); return Task.CompletedTask; });

        _pipeline.Add("GET", "/api/yield/decompose", ctx => { CountHit(); ServeYieldDecompose(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/yield/decompose/config", ctx => { CountHit(); ServeYieldDecomposeConfigGet(ctx); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/yield/decompose/config", ctx => { CountHit(); ServeYieldDecomposeConfig(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/yield/attribution/*", ctx => { CountHit(); ServeYieldAttribution(ctx); return Task.CompletedTask; });

        _pipeline.Add("GET", "/api/devices/health", ctx => { CountHit(); ServeDeviceHealth(ctx); return Task.CompletedTask; });

        _pipeline.Add("POST", "/api/login", ctx => { CountHit(); return ServeLogin(ctx); });
        _pipeline.Add("GET", "/api/status", ctx => { CountHit(); ServeStatus(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/metrics", ctx => { CountHit(); ServeMetrics(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/audit", ctx => { CountHit(); ServeAudit(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/users", ctx => { CountHit(); return ServeUsers(ctx); });
        _pipeline.Add("POST", "/api/users", ctx => { CountHit(); return ServeUsers(ctx); });
        _pipeline.Add("DELETE", "/api/users", ctx => { CountHit(); return ServeUsers(ctx); });

        _pipeline.Add("POST", "/api/mesh/fail", ctx => { CountHit(); ServeMeshPush(ctx, "fail"); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/mesh/heartbeat", ctx => { CountHit(); ServeMeshPush(ctx, "heartbeat"); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/mesh/event", ctx => { CountHit(); ServeMeshEvent(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/mesh/summary", ctx => { CountHit(); ServeMeshSummary(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/mesh/fetch", ctx => { CountHit(); ServeMeshFetch(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/mesh/peers", ctx => { CountHit(); ServeMeshPeers(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/mesh/xml", ctx => { CountHit(); ServeMeshXml(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/todos/sync", ctx => { CountHit(); ServeTodos(ctx); return Task.CompletedTask; });

        _pipeline.Add("POST", "/api/mesh/query", ctx => { CountHit(); return ServeMeshQuery(ctx); });
        _pipeline.Add("POST", "/api/mesh/query/local", ctx => { CountHit(); return ServeMeshQueryLocal(ctx); });

        _pipeline.Add("GET", "/api/maintenance", ctx => { CountHit(); ServeMaintenanceList(ctx); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/maintenance", ctx => { CountHit(); return ServeMaintenanceCreate(ctx); });
        _pipeline.Add("PATCH", "/api/maintenance", ctx => { CountHit(); return ServeMaintenanceUpdate(ctx); });
        _pipeline.Add("PUT", "/api/maintenance", ctx => { CountHit(); return ServeMaintenanceUpdate(ctx); });
        _pipeline.Add("DELETE", "/api/maintenance", ctx => { CountHit(); ServeMaintenanceDelete(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/maintenance/counts", ctx => { CountHit(); ServeMaintenanceCounts(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/todos", ctx => { CountHit(); ServeTodoList(ctx); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/todos", ctx => { CountHit(); return ServeTodoCreate(ctx); });
        _pipeline.Add("POST", "/api/todos/ack", ctx => { CountHit(); return ServeTodoAck(ctx); });
        _pipeline.Add("DELETE", "/api/todos", ctx => { CountHit(); ServeTodoDelete(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/resolvers", ctx => { CountHit(); ServeResolvers(ctx); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/resolvers", ctx => { CountHit(); return ServeResolversCreate(ctx); });
        _pipeline.Add("DELETE", "/api/resolvers", ctx => { CountHit(); ServeResolversDelete(ctx); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/resolvers/rename", ctx => { CountHit(); return ServeResolversRename(ctx); });
        _pipeline.Add("GET", "/api/export/maintenance", ctx => { CountHit(); ServeExportMaintenance(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/export.maintenance", ctx => { CountHit(); ServeExportMaintenance(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/maintenance/export", ctx => { CountHit(); ServeExportMaintenance(ctx); return Task.CompletedTask; });

        #region Agent C: P6 设备监控 + P2-A 服务化 + P7 数据拉取雏形（2026-08-28）— 单一 region，A/B 禁改
        _pipeline.Add("POST", "/api/mesh/info", ctx => { CountHit(); ServeMeshInfo(ctx); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/mesh/fctini", ctx => { CountHit(); ServeMeshFctIni(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/devices", ctx => { CountHit(); ServeDevices(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/devices/*", ctx => { CountHit(); ServeDeviceWildcard(ctx); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/fetch", ctx => { CountHit(); return ServeFetchCreate(ctx); });
        _pipeline.Add("GET", "/api/fetch/jobs", ctx => { CountHit(); ServeFetchJobs(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/fetch/status", ctx => { CountHit(); ServeFetchStatus(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/trends", ctx => { CountHit(); ServeTrends(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/distribution", ctx => { CountHit(); ServeDistribution(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/mesh/trends", ctx => { CountHit(); ServeTrends(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/mesh/distribution", ctx => { CountHit(); ServeDistribution(ctx); return Task.CompletedTask; });
        #endregion

        #region Lite-Fetch: 数据拉取完整 + 报告中心 + 程序日志路由（2026-08-28）— 单一 region，A/B/C 禁改
        _pipeline.Add("GET", "/api/fetch/download", ctx => { CountHit(); ServeFetchDownload(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/fetch/progress", ctx => { CountHit(); ServeFetchProgress(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/heatmap", ctx => { CountHit(); ServeHeatmap(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/mesh/heatmap", ctx => { CountHit(); ServeHeatmap(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/report/summary", ctx => { CountHit(); ServeReportSummary(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/report/archive", ctx => { CountHit(); ServeReportArchiveList(ctx); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/report/archive", ctx => { CountHit(); return ServeReportArchiveCreate(ctx); });
        _pipeline.Add("GET", "/api/report/compare", ctx => { CountHit(); ServeReportCompare(ctx); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/proc-log", ctx => { CountHit(); return ServeProcLogCreate(ctx); });
        _pipeline.Add("GET", "/api/proc-log", ctx => { CountHit(); ServeProcLogList(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/proc-log/timeline", ctx => { CountHit(); ServeProcLogTimeline(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/proc-log/diff", ctx => { CountHit(); ServeProcLogDiff(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/proc-log/detail", ctx => { CountHit(); ServeProcLogDetail(ctx); return Task.CompletedTask; });
        _pipeline.Add("DELETE", "/api/proc-log", ctx => { CountHit(); var idStr = ctx.Request.QueryString["id"] ?? ""; if (long.TryParse(idStr, out var did) && _db.DeleteProcLog(did)) RespondText(ctx, 200, "ok"); else RespondText(ctx, 404, "not found"); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/proc_log", ctx => { CountHit(); ServeProcLogList(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/proc_log/timeline", ctx => { CountHit(); ServeProcLogTimeline(ctx); return Task.CompletedTask; });
        #endregion

        #region Lite-Settings: 剩余前端设置与体验层（2026-08-28）— 单一 region，A/B/C 禁改
        _pipeline.Add("GET", "/api/users/me", ctx => { CountHit(); return ServeUsersMe(ctx); });
        _pipeline.Add("GET", "/api/users/me/layout", ctx => { CountHit(); return ServeUsersMeLayoutGet(ctx); });
        _pipeline.Add("PATCH", "/api/users/me/layout", ctx => { CountHit(); return ServeUsersMeLayoutPatch(ctx); });
        _pipeline.Add("PUT", "/api/users/me/layout", ctx => { CountHit(); return ServeUsersMeLayoutPatch(ctx); });
        _pipeline.Add("POST", "/api/users/me/layout", ctx => { CountHit(); return ServeUsersMeLayoutPatch(ctx); });
        _pipeline.Add("GET", "/api/users/me/favorites", ctx => { CountHit(); return ServeUsersMeFavGet(ctx); });
        _pipeline.Add("PATCH", "/api/users/me/favorites", ctx => { CountHit(); return ServeUsersMeFavPatch(ctx); });
        _pipeline.Add("PUT", "/api/users/me/favorites", ctx => { CountHit(); return ServeUsersMeFavPatch(ctx); });
        _pipeline.Add("POST", "/api/users/me/favorites", ctx => { CountHit(); return ServeUsersMeFavPatch(ctx); });
        _pipeline.Add("GET", "/api/search", ctx => { CountHit(); ServeSearch(ctx); return Task.CompletedTask; });
        #endregion

        #region Lite-Infra: MeshPusher并发+ Gossiper自适应 + 告警规则中心 + 多机台对比（2026-08-28）— 单一 region，A/B/C/Lite-Fetch/Lite-Settings 禁改
        _pipeline.Add("GET", "/api/alerts/rules", ctx => { CountHit(); ServeAlertRules(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/alerts/history", ctx => { CountHit(); ServeAlertHistory(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/gossiper/status", ctx => { CountHit(); ServeGossiperStatus(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/compare/trends", ctx => { CountHit(); ServeCompareTrends(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/compare/distribution", ctx => { CountHit(); ServeCompareDistribution(ctx); return Task.CompletedTask; });
        #endregion

        #region Lite-Ops: 告警规则热更新（2026-08-28）— 单一 region，A/B/C/Lite-Fetch/Lite-Settings/Lite-Infra 禁改
        _pipeline.Add("PATCH", "/api/alerts/rules", ctx => { CountHit(); ServeAlertRulesPatch(ctx); return Task.CompletedTask; });
        _pipeline.Add("PUT", "/api/alerts/rules", ctx => { CountHit(); ServeAlertRulesPatch(ctx); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/alerts/rules", ctx => { CountHit(); ServeAlertRulesPatch(ctx); return Task.CompletedTask; });
        #endregion

        _pipeline.Add("GET", "/public/*", ctx =>
        {
            CountHit();
            var p = ctx.Request.Url?.AbsolutePath ?? "";
            if (p.Length > 1) p = p.TrimEnd('/');
            if (p.Length == 0) p = "/";
            ServePublicFile(ctx, p);
            return Task.CompletedTask;
        });
    }

    public bool Listening
    {
        get { lock (_lock) return _listener != null && _listener.IsListening; }
    }

    public long ReceivedCount => Interlocked.Read(ref _receivedCount);

    public event Action? SettingsChanged;

    public void Start()
    {
        lock (_lock)
        {
            if (_listener != null) return;
            var attempts = new[]
            {
                $"http://+:{_port}/",
                $"http://127.0.0.1:{_port}/",
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
                    if (_token.Length == 0)
                        Logger.Warning("[Web 服务] agg_token 未配置，当前为未鉴权模式（任意客户端可访问），请在设置页配置token");
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

    private static readonly SemaphoreSlim ConcurrencyGate = new(64, 64);
    private static long _rejected503;

    public static long Rejected503Count => Interlocked.Read(ref _rejected503);

    private void AcceptLoop()
    {
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = _listener!.GetContext(); }
            catch { break; }
            if (!ConcurrencyGate.Wait(0))
            {
                Interlocked.Increment(ref _rejected503);
                try
                {
                    ctx.Response.StatusCode = 503;
                    ctx.Response.KeepAlive = false;
                    ctx.Response.Close();
                }
                catch { }
                continue;
            }
            _ = Task.Run(() =>
            {
                try { Handle(ctx); }
                finally { ConcurrencyGate.Release(); }
            });
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            using var resp = ctx.Response;

            var absPath = ctx.Request.Url?.AbsolutePath ?? "";
            var loginExempt = absPath.Equals("/api/login", StringComparison.OrdinalIgnoreCase);
            var staticExempt = absPath.Equals("/", StringComparison.OrdinalIgnoreCase)
                || absPath.StartsWith("/public/", StringComparison.OrdinalIgnoreCase)
                || absPath.Equals("/public", StringComparison.OrdinalIgnoreCase);
            if (!loginExempt && !staticExempt && !IsAuthenticated(ctx))
            {
                Logger.Warning($"[Web 服务] 403: 鉴权缺失或不匹配（{ctx.Request.RemoteEndPoint} {ctx.Request.HttpMethod} {ctx.Request.Url?.AbsolutePath}）");
                resp.StatusCode = 403;
                return;
            }
            if (_token.Length > 0 || _db.ListUsers().Count > 0) IssueTokenCookie(resp, ExtractToken(ctx));

            _pipeline.Handle(ctx);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 请求处理异常: {ex.Message}");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    private void CountHit() => Interlocked.Increment(ref _receivedCount);

    private void ServePublicFile(HttpListenerContext ctx, string path)
    {
        try
        {
            var publicRoot = Path.GetFullPath(Path.Combine(AppConfig.BaseDir, "public"));
            var rel = path.Substring("/public".Length).TrimStart('/', '\\');
            if (rel.Length == 0) rel = "index.html";
            var full = Path.GetFullPath(Path.Combine(publicRoot, rel));
            if (Directory.Exists(full))
            {
                full = Path.GetFullPath(Path.Combine(full, "index.html"));
            }
            if (!full.StartsWith(publicRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warning($"[Web 服务] 静态资源越界拒绝: {path}");
                ctx.Response.StatusCode = 403;
                return;
            }
            if (!File.Exists(full))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            var ext = Path.GetExtension(full).ToLowerInvariant();
            var mime = ext switch
            {
                ".html" or ".htm" => "text/html; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".js" => "application/javascript; charset=utf-8",
                ".json" => "application/json; charset=utf-8",
                ".svg" => "image/svg+xml",
                ".png" => "image/png",
                ".ico" => "image/x-icon",
                ".txt" or ".md" => "text/plain; charset=utf-8",
                _ => "application/octet-stream",
            };
            ctx.Response.ContentType = mime;
            ctx.Response.AddHeader("X-Content-Type-Options", "nosniff");
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.StatusCode = 200;
            using (var fs = File.OpenRead(full))
            {
                ctx.Response.ContentLength64 = fs.Length;
                fs.CopyTo(ctx.Response.OutputStream);
            }
            ctx.Response.OutputStream.Close();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 静态资源服务失败 {path}: {ex.Message}");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    internal const string TokenCookieName = "agg_token";

    private static string ExtractToken(HttpListenerContext ctx)
    {
        var got = ctx.Request.Headers[HttpIngest.TokenHeader];
        if (string.IsNullOrEmpty(got)) got = ctx.Request.QueryString["token"] ?? "";
        if (string.IsNullOrEmpty(got))
        {
            var ck = ctx.Request.Cookies[TokenCookieName];
            if (ck != null) got = ck.Value ?? "";
        }
        return got ?? "";
    }

    private bool IsAuthenticated(HttpListenerContext ctx)
    {
        if (_token.Length == 0 && _db.ListUsers().Count == 0) return true;
        var got = ExtractToken(ctx);
        if (!string.IsNullOrEmpty(got))
        {
            if (_token.Length > 0 && FixedTimeEquals(got, _token)) return true;
            if (_db.GetUserByToken(got) != null) return true;
        }
        return false;
    }

    private (string? role, string? who) ResolveRole(HttpListenerContext ctx)
    {
        var got = ExtractToken(ctx);
        if (!string.IsNullOrEmpty(got))
        {
            if (_token.Length > 0 && FixedTimeEquals(got, _token)) return ("admin", "agg_token");
            var u = _db.GetUserByToken(got);
            if (u != null) return (u.Role, u.Name);
        }
        if (_token.Length == 0 && _db.ListUsers().Count == 0) return ("admin", "anonymous");
        return (null, null);
    }

    private static int RoleLevel(string role) => role switch
    {
        "viewer" => 1,
        "engineer" => 2,
        "admin" => 3,
        _ => 0,
    };

    private bool RequireRole(HttpListenerContext ctx, string minRole)
    {
        var (role, _) = ResolveRole(ctx);
        if (role == null || RoleLevel(role) < RoleLevel(minRole))
        {
            Logger.Warning($"[Web 服务] 403: 角色不足（{ctx.Request.RemoteEndPoint} {ctx.Request.HttpMethod} {ctx.Request.Url?.AbsolutePath}，需要 {minRole}）");
            RespondText(ctx, 403, $"forbidden: need role {minRole}");
            return false;
        }
        return true;
    }

    private static bool FixedTimeEquals(string? a, string b)
    {
        if (string.IsNullOrEmpty(a)) return false;
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ba.Length != bb.Length) return false;
        int diff = 0;
        for (int i = 0; i < ba.Length; i++) diff |= ba[i] ^ bb[i];
        return diff == 0;
    }

    private void IssueTokenCookie(HttpListenerResponse resp, string tokenValue)
    {
        if (string.IsNullOrEmpty(tokenValue)) return;
        try { resp.Headers["Set-Cookie"] = $"{TokenCookieName}={tokenValue}; Path=/; HttpOnly; SameSite=Strict"; }
        catch { }
    }

    private void ServeDashboard(HttpListenerContext ctx)
    {
        var spaIndex = Path.Combine(AppConfig.BaseDir, "public", "index.html");
        if (File.Exists(spaIndex))
        {
            var qs = ctx.Request.Url?.Query ?? "";
            try
            {
                ctx.Response.StatusCode = 302;
                ctx.Response.Headers["Location"] = "/public/" + qs;
                ctx.Response.OutputStream.Close();
            }
            catch (Exception ex) { Logger.Warning($"[Web 服务] 跳转 /public/ 失败: {ex.Message}"); }
            return;
        }
        ServeLegacyDashboard(ctx);
    }

    private void ServeLegacyDashboard(HttpListenerContext ctx)
    {
        var page = DashboardHtml
            .Replace("%%SHARE_ROOT%%", WebUtility.HtmlEncode(_shareRoot))
            .Replace("%%DB_PATH%%", WebUtility.HtmlEncode(_db.DbPath))
            .Replace("%%RESULTS_ROOT%%", WebUtility.HtmlEncode(_resultsRoot))
            .Replace("%%RESULTS_ROOT_JS%%", (_resultsRoot ?? "")
                .Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("<", "\\u003c").Replace("/", "\\u002f"));
        Respond(ctx, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(page));
    }

    private void ServeHealth(HttpListenerContext ctx)
    {
        var uptime = _startedAt == default ? 0 : (long)(DateTime.Now - _startedAt).TotalSeconds;
        var payload = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["uptime_sec"] = uptime,
            ["received"] = ReceivedCount,
        };
        Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
    }

    private void ServeXmlView(HttpListenerContext ctx)
    {
        try
        {
            var q = ctx.Request.QueryString;
            var pathParam = q["path"];
            if (!string.IsNullOrEmpty(pathParam))
            {
                var machineHint = q["machine"] ?? "";
                string? xml2 = null;
                if (ResolveFile(pathParam, out _) == 200 && File.Exists(pathParam))
                {
                    try { xml2 = File.ReadAllText(pathParam, Encoding.UTF8); } catch { }
                }
                if (string.IsNullOrEmpty(xml2) && _mesh != null)
                    xml2 = FetchRemoteXmlByPath(machineHint, pathParam);
                if (string.IsNullOrEmpty(xml2))
                { RespondText(ctx, 404, "XML 报告不可用（源机离线或路径不在白名单）"); return; }
                var data2 = XmlParser.ParseReportText(xml2);
                if (data2.Error) { RespondText(ctx, 500, "XML 解析失败"); return; }
                var fileName2 = Path.GetFileName(pathParam);
                var rawUrl2 = $"/api/file?path={Uri.EscapeDataString(pathParam)}";
                var html2 = XmlReportHtml.Render(data2, fileName2, rawUrl2);
                Respond(ctx, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html2));
                return;
            }

            if (!long.TryParse(q["id"], out var id) || id <= 0)
            { RespondText(ctx, 400, "缺少有效 id 参数"); return; }

            var row = _db.GetFailById(id);
            if (row == null)
            { RespondText(ctx, 404, "报告不存在（id 不在聚合库）"); return; }

            string? xml = null;
            if (_mesh != null)
            {
                xml = _mesh.Receiver.FetchXmlForFail(id);
            }
            else
            {
#pragma warning disable CS0618
                if (!string.IsNullOrEmpty(row.XmlPath) && ResolveFile(row.XmlPath, out _) == 200 && File.Exists(row.XmlPath))
                    xml = File.ReadAllText(row.XmlPath, Encoding.UTF8);
#pragma warning restore CS0618
            }
            if (string.IsNullOrEmpty(xml))
            { RespondText(ctx, 404, "XML 报告不可用（源机离线或路径不在白名单）"); return; }

            var data = XmlParser.ParseReportText(xml);
            if (data.Error)
            { RespondText(ctx, 500, "XML 解析失败"); return; }

            var fileName = Path.GetFileName(string.IsNullOrEmpty(row.XmlPath) ? "report.xml" : row.XmlPath);
            var html = XmlReportHtml.Render(data, fileName, $"/api/file?id={id}");
            Respond(ctx, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 在线 XML 查看失败 id={ctx.Request.QueryString["id"]} path={ctx.Request.QueryString["path"]}: {ex.Message}");
            RespondText(ctx, 500, "在线查看失败");
        }
    }

    private string? FetchRemoteXmlByPath(string machineHint, string path)
    {
        if (_mesh == null) return null;
        var urls = _mesh.PeerUrls;
        if (urls.Length == 0) return null;
        foreach (var peer in urls)
        {
            try
            {
                var url = peer.TrimEnd('/') + "/api/file?path=" + Uri.EscapeDataString(path);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(_token)) req.Headers.Add(MeshPusher.TokenHeader, _token);
                using var resp = MeshPusher.SendStatic(req);
                if (resp.IsSuccessStatusCode)
                {
                    var txt = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!string.IsNullOrEmpty(txt)) return txt;
                }
            }
            catch { }
        }
        return null;
    }

    private void ServeSettings(HttpListenerContext ctx)
    {
        try
        {
            var cfg = AppConfig.Instance;
            var payload = new Dictionary<string, object?>
            {
                ["mesh_port"] = _port,
                ["agg_token_set"] = cfg.AggToken.Length > 0,
                ["agg_token"] = "",
                ["agg_webhook_set"] = cfg.AggWebhookUrl.Length > 0,
                ["agg_webhook_url"] = cfg.AggWebhookUrl,
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

    private void ServeSettingsSave(HttpListenerContext ctx)
    {
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException)
        {
            ctx.Response.StatusCode = 413;
            ctx.Response.KeepAlive = false;
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var cfg = AppConfig.Instance;
            var changed = new List<string>();

            if (root.TryGetProperty("mesh_port", out var pp) && pp.ValueKind == JsonValueKind.Number
                && pp.TryGetInt32(out var port) && port >= 1 && port <= 65535 && port != cfg.MeshPort)
            {
                cfg.MeshPort = port;
                changed.Add("mesh_port");
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
                var hook = (pw.GetString() ?? "").Trim();
                if (hook.Length > 0 && !hook.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = 400;
                    RespondText(ctx, 400, "agg_webhook_url 只支持 https:// 地址");
                    return;
                }
                cfg.AggWebhookUrl = hook;
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

            bool needRestart = changed.Contains("mesh_port") || changed.Contains("agg_transport");
            if (changed.Contains("agg_token")) _token = cfg.AggToken;
            try { SettingsChanged?.Invoke(); } catch (Exception ex) { Logger.Warning($"[Web 服务] 设置变更回调异常: {ex.Message}"); }

            try { var (_, who) = ResolveRole(ctx); _db.LogAudit(who ?? "?", "settings.save", string.Join(",", changed)); }
            catch (Exception ex) { Logger.Warning($"[Web 服务] 审计写入失败: {ex.Message}"); }

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

    private void ServeConfigValidate(HttpListenerContext ctx)
    {
        try
        {
            var cfg = AppConfig.Instance;
            var errs = ConfigValidator.Validate(cfg);
            var resp = new { ok = errs.Count==0, errors = errs };
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(resp, JsonOpts));
        } catch (Exception ex) { RespondText(ctx, 500, ex.Message); }
    }
    private void ServeConfigRecommend(HttpListenerContext ctx)
    {
        try
        {
            var cfg = AppConfig.Instance;
            var recs = ConfigAdvisor.Recommend(cfg, _db);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, recommends = recs }, JsonOpts));
        } catch (Exception ex) { RespondText(ctx, 500, ex.Message); }
    }
    private void ServeConfigBackups(HttpListenerContext ctx)
    {
        try
        {
            var list = AppConfig.ListBackups(20).Select(f=> new { file = Path.GetFileName(f), path = f, time = File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm:ss") });
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, backups = list }, JsonOpts));
        } catch (Exception ex) { RespondText(ctx, 500, ex.Message); }
    }
    private void ServeConfigRollback(HttpListenerContext ctx)
    {
        string body; try { body = ReadBody(ctx.Request); } catch (BodyTooLargeException) { ctx.Response.StatusCode=413; ctx.Response.KeepAlive=false; return; }
        try
        {
            string? target = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try { using var doc = JsonDocument.Parse(body); if (doc.RootElement.TryGetProperty("file", out var v) && v.ValueKind==JsonValueKind.String) target = v.GetString(); } catch {}
                if (string.IsNullOrEmpty(target)) target = body.Trim().Trim('\"');
                if (!string.IsNullOrEmpty(target) && !Path.IsPathRooted(target))
                    target = Path.Combine(AppConfig.BaseDir, "data", "config_backups", Path.GetFileName(target));
            }
            var ok = AppConfig.Rollback(target);
            if (ok) try { SettingsChanged?.Invoke(); } catch {}
            Respond(ctx, ok?200:404, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(ok? "{\"ok\":true}":"{\"ok\":false,\"msg\":\"not found\"}"));
        } catch (Exception ex) { RespondText(ctx, 500, ex.Message); }
    }
    private void ServeDevicesPredict(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var preds = DevicePredictor.Predict(_db, 7);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, predicts = preds }, JsonOpts));
        } catch (Exception ex) { RespondText(ctx, 500, ex.Message); }
    }
    private void ServeDevicesInspect(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var sug = DeviceInspector.Inspect(_db);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, suggests = sug }, JsonOpts));
        } catch (Exception ex) { RespondText(ctx, 500, ex.Message); }
    }
    private void ServeFctChanges(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var q = ctx.Request.QueryString;
            var machine = (q["machine"] ?? "").Trim();
            int limit = 100; if(int.TryParse(q["limit"], out var l) && l>0) limit=Math.Min(l,500);
            var list = _db.QueryFctChanges(string.IsNullOrEmpty(machine)?null:machine, limit);
            var dto = list.Select(x=> new { id=x.id, ts=x.ts, machine=x.machine, detail=x.detail, hash=x.hash });
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, changes = dto }, JsonOpts));
        } catch (Exception ex) { RespondText(ctx, 500, ex.Message); }
    }
    private void ServeTodoSuggest(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try{
            var list = TodoSuggester.Suggest(_db, 30);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok=true, suggests=list }, JsonOpts));
        } catch(Exception ex){ RespondText(ctx, 500, ex.Message); }
    }
    private void ServeMaintenanceAdvise(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try{
            var q=ctx.Request.QueryString;
            string cur = (q["status"] ?? q["current"] ?? "open").Trim();
            if(string.IsNullOrEmpty(cur)) cur="open";
            var idStr = q["id"] ?? "";
            if(long.TryParse(idStr, out var id) && id>0){
                var rec = _db.GetMaintenance((int)id);
                if(rec!=null) cur = rec.Status;
            }
            var adv = FlowAdvisor.Advise(cur, _db);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok=true, advise=adv }, JsonOpts));
        } catch(Exception ex){ RespondText(ctx, 500, ex.Message); }
    }
    private void ServeHighlights(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try{
            var list = HighlightEngine.GetHighlights(_db, AppConfig.Instance);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok=true, highlights=list }, JsonOpts));
        } catch(Exception ex){ RespondText(ctx, 500, ex.Message); }
    }
    private void ServeLayoutSuggest(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try{
            var q=ctx.Request.QueryString;
            var role = (q["role"] ?? "viewer").Trim();
            string visitsJson = "";
            try{ visitsJson = System.IO.File.ReadAllText(System.IO.Path.Combine(AppConfig.BaseDir, "data", "layout_visits.json")); } catch{}
            var freq = LayoutAdvisor.ParseFreq(visitsJson);
            try{
                var who = ResolveRole(ctx).Item2;
                if(!string.IsNullOrEmpty(who)){
                    var u = _db.GetUserByName(who);
                    if(u!=null && !string.IsNullOrEmpty(u.Layout)){
                        var lay = System.Text.Json.JsonDocument.Parse(u.Layout);
                        if(lay.RootElement.TryGetProperty("visits", out var v) && v.ValueKind==System.Text.Json.JsonValueKind.Object){
                            foreach(var prop in v.EnumerateObject()) if(prop.Value.ValueKind==System.Text.Json.JsonValueKind.Number && prop.Value.TryGetInt32(out var c)) freq[prop.Name]=c;
                        }
                    }
                }
            } catch{}
            var order = LayoutAdvisor.SuggestOrder(freq, role);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok=true, order=order, freq=freq }, JsonOpts));
        } catch(Exception ex){ RespondText(ctx, 500, ex.Message); }
    }
    private void ServeAlertsPredict(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try{
            var preds = AlertPredictor.Predict(_db);
            try{ AlertPredictor.LogPredictions(_db, preds); } catch{}
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok=true, predicts=preds }, JsonOpts));
        } catch(Exception ex){ RespondText(ctx, 500, ex.Message); }
    }
    private void ServeAlertsHeal(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try{
            var q=ctx.Request.QueryString;
            var machine=(q["machine"]??"").Trim();
            var rule=(q["rule"]??"disk").Trim();
            if(string.IsNullOrEmpty(machine)){ RespondText(ctx, 400, "machine required"); return; }
            var sug = AlertHealer.Heal(_db, machine, rule);
            var feishu = AlertHealer.FormatForFeishu(machine, rule, sug);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok=true, heals=sug, feishu=feishu }, JsonOpts));
        } catch(Exception ex){ RespondText(ctx, 500, ex.Message); }
    }

    private void ServeMachines(HttpListenerContext ctx)
    {
        try
        {
            object list;
            if (_mesh != null) list = _mesh.PeerStatuses;
            else
            {
#pragma warning disable CS0618
                list = _watcher!.GetMachines();
#pragma warning restore CS0618
            }
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(list, JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 查询机台状态失败: {ex.Message}");
            RespondText(ctx, 500, "查询机台状态失败");
        }
    }

    private void ServeStats(HttpListenerContext ctx)
    {
        try
        {
            var q = ctx.Request.QueryString;
            var machine = (q["machine"] ?? "").Trim();
            var from = (q["from"] ?? "").Trim();
            var to = (q["to"] ?? "").Trim();
            var max = 2000;
            if (int.TryParse(q["max"], out var m) && m > 0) max = Math.Min(m, 5000);
            var rows = _db.QueryDailyStats(
                machine.Length == 0 ? null : machine,
                from.Length == 0 ? null : from,
                to.Length == 0 ? null : to,
                max);
            var dto = rows.Select(r => new
            {
                r.Machine, r.TestDate, r.Total, r.Pass, r.Fail, r.Interrupted, r.Products, r.UpdatedTs,
                Yield = r.Total > 0 ? Math.Round(r.Pass * 100.0 / r.Total, 2) : 100.0,
            });
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(dto, JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 查询日统计失败: {ex.Message}");
            RespondText(ctx, 500, "查询日统计失败");
        }
    }

    private void ServePredictAccuracy(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var q = ctx.Request.QueryString;
            var days = 30;
            if (int.TryParse(q["days"], out var d) && d > 0) days = Math.Min(d, 180);
            var force = string.Equals(q["force"], "1", StringComparison.Ordinal);

            var cfg = AppConfig.Instance;
            if (force || DateTime.Now.Hour == cfg.PredictReconcileCronHour)
            {
                try { PredictAccuracyReconciler.RunOnce(_db, cfg); } catch {  }
            }

            var summary = new PredictAccuracyReconciler.ReconcileSummary
            {
                WindowDays = days,
                Summary = new System.Collections.Generic.Dictionary<string, PredictAccuracyReconciler.RuleStat>(),
                PerMachine = new System.Collections.Generic.List<PredictAccuracyReconciler.MachineHitRate>(),
                ThresholdTuning = new System.Collections.Generic.List<PredictAccuracyReconciler.ThresholdTune>(),
                GeneratedAt = DateTime.Now,
            };
            foreach (var rule in new[] { "yield", "cpu", "disk", "offline" })
            {
                var (total, hit, lead) = _db.CountPredictAccuracyByRule(rule, days);
                summary.Summary[rule] = new PredictAccuracyReconciler.RuleStat
                {
                    Total = total,
                    Hit = hit,
                    Accuracy = total > 0 ? Math.Round((double)hit / total, 4) : 0.0,
                    AvgLeadDays = Math.Round(lead, 2),
                };
            }
            var recent = _db.QueryPredictAccuracy(days: days, limit: 5000);
            foreach (var g in recent.GroupBy(r => (r.Machine, r.Rule)))
            {
                var ordered = g.OrderByDescending(r => r.ReconciledAt).ToList();
                var hits = ordered.Count(r => r.Hit);
                var total = ordered.Count;
                var streak = 0;
                foreach (var r in ordered) { if (!r.Hit) streak++; else break; }
                summary.PerMachine.Add(new PredictAccuracyReconciler.MachineHitRate
                {
                    Machine = g.Key.Machine,
                    Rule = g.Key.Rule,
                    HitRate = total > 0 ? Math.Round((double)hits / total, 4) : 0.0,
                    MissStreak = streak,
                });
            }
            if (cfg.PredictTuneEnabled)
            {
                RecommendThresholdTuning(summary, cfg);
            }

            Respond(ctx, 200, "application/json; charset=utf-8",
                JsonSerializer.SerializeToUtf8Bytes(summary, JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 预测准确率查询失败: {ex.Message}");
            RespondText(ctx, 500, "predict accuracy query failed");
        }
    }

    private static void RecommendThresholdTuning(PredictAccuracyReconciler.ReconcileSummary summary, AppConfig cfg)
    {
        if (!summary.Summary.TryGetValue("yield", out var stat)) return;
        var current = cfg.YieldAlertYieldPct;
        if (stat.Total < cfg.PredictTuneMinSamples) return;
        if (stat.Accuracy < 0.30 && current < 99.0)
        {
            summary.ThresholdTuning.Add(new PredictAccuracyReconciler.ThresholdTune
            {
                Rule = "yield",
                Current = Math.Round(current, 2),
                Recommended = Math.Round(Math.Min(99.0, current + 5.0), 2),
                Reason = $"命中率 {stat.Accuracy:P0} < 30%（{stat.Total} 样本），放宽阈值减少误报",
            });
        }
        else if (stat.Accuracy > 0.85 && stat.Total >= cfg.PredictTuneMinSamples && current > 50.0)
        {
            summary.ThresholdTuning.Add(new PredictAccuracyReconciler.ThresholdTune
            {
                Rule = "yield",
                Current = Math.Round(current, 2),
                Recommended = Math.Round(Math.Max(50.0, current - 5.0), 2),
                Reason = $"命中率 {stat.Accuracy:P0} > 85%（{stat.Total} 样本），收紧阈值减少漏报",
            });
        }
    }

    private void ServeYieldDecompose(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var q = ctx.Request.QueryString;
            var machine = (q["machine"] ?? "").Trim();
            if (string.IsNullOrEmpty(machine)) { RespondText(ctx, 400, "machine required"); return; }
            var cfg = AppConfig.Instance;
            var mode = YieldDecomposer.ParseMode(q["mode"] ?? cfg.YieldSeasonalityMode);
            var days = cfg.YieldSeasonalityDays;
            if (int.TryParse(q["days"], out var d) && d >= 7) days = Math.Min(d, 90);
            var dec = YieldDecomposer.Decompose(_db, machine, mode, days,
                cfg.YieldSeasonalityTrendWindow, cfg.YieldSeasonalityEpsilon);
            Respond(ctx, 200, "application/json; charset=utf-8",
                JsonSerializer.SerializeToUtf8Bytes(dec, JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 季节性分解失败: {ex.Message}");
            RespondText(ctx, 500, "seasonality decompose failed");
        }
    }

    private void ServeYieldAttribution(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            string machine = "";
            var parts = path.Split('/');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] == "attribution" && i + 1 < parts.Length)
                {
                    machine = parts[i + 1];
                    break;
                }
            }

            if (string.IsNullOrEmpty(machine)) { RespondText(ctx, 400, "machine parameter required"); return; }

            int daysBack = 7;
            if (int.TryParse(ctx.Request.QueryString["days"], out var d) && d >= 1)
                daysBack = Math.Min(d, 90);

            var breakdown = YieldAttributor.AnalyzeModel(_db, machine, daysBack);

            var resp = new
            {
                machine,
                days_back = daysBack,
                total_fail = _db.GetRecentFailCount(machine, daysBack),
                items = breakdown.Select(x => new
                {
                    x.Rank,
                    x.Model,
                    x.Category,
                    x.Total,
                    x.Pass,
                    x.Fail,
                    x.Interrupted,
                    yield_pct = Math.Round(x.YieldPct, 2),
                    contribution = Math.Round(x.Contribution, 1)
                })
            };

            Respond(ctx, 200, "application/json; charset=utf-8",
                JsonSerializer.SerializeToUtf8Bytes(resp, JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 良率归因失败：{ex.Message}");
            RespondText(ctx, 500, "yield attribution failed");
        }
    }

    #region v3.22.0 规格04：设备健康综合分（单一 region，A/B/C/Lite-* 禁改）

    private void ServeDeviceHealth(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var report = DeviceHealthScorer.Score(_db, AppConfig.Instance);
            var resp = new
            {
                machines = report.Machines.Select(m => new
                {
                    machine = m.Machine,
                    health = m.Health,
                    level = m.Level,
                    components = m.Components.Select(c => new
                    {
                        name = c.Name,
                        score = Math.Round(c.Score, 1),
                        weight = c.Weight,
                        raw = c.Raw,
                        trend = c.Trend,
                    }),
                    top_concern = m.TopConcern,
                    recommendation = m.Recommendation,
                }),
                summary = new { ok = report.Summary.Ok, warn = report.Summary.Warn, critical = report.Summary.Critical },
                generated_at = report.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            };
            Respond(ctx, 200, "application/json; charset=utf-8",
                JsonSerializer.SerializeToUtf8Bytes(resp, JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 设备健康分查询失败：{ex.Message}");
            RespondText(ctx, 500, "device health failed");
        }
    }

    #endregion

    private void ServeYieldDecomposeConfigGet(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        var cfg = AppConfig.Instance;
        var resp = new Dictionary<string, object?>
        {
            ["enabled"] = cfg.YieldSeasonalityEnabled,
            ["mode"] = cfg.YieldSeasonalityMode,
            ["epsilon"] = cfg.YieldSeasonalityEpsilon,
            ["days"] = cfg.YieldSeasonalityDays,
            ["min_sigma"] = cfg.YieldSeasonalityMinSigma,
        };
        Respond(ctx, 200, "application/json; charset=utf-8",
            JsonSerializer.SerializeToUtf8Bytes(resp, JsonOpts));
    }

    private void ServeYieldDecomposeConfig(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "admin")) return;
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException)
        {
            ctx.Response.StatusCode = 413;
            ctx.Response.KeepAlive = false;
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var cfg = AppConfig.Instance;
            var changed = new List<string>();
            if (root.TryGetProperty("enabled", out var pe) &&
                (pe.ValueKind == JsonValueKind.True || pe.ValueKind == JsonValueKind.False))
            {
                cfg.YieldSeasonalityEnabled = pe.ValueKind == JsonValueKind.True;
                changed.Add("yield_seasonality_enabled");
            }
            if (root.TryGetProperty("mode", out var pm) && pm.ValueKind == JsonValueKind.String)
            {
                var m = (pm.GetString() ?? "").Trim().ToLowerInvariant();
                if (m != "hourly" && m != "daily" && m != "weekly")
                {
                    RespondText(ctx, 400, "mode 必须为 hourly|daily|weekly");
                    return;
                }
                cfg.YieldSeasonalityMode = m;
                changed.Add("yield_seasonality_mode");
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
            try { var (_, who) = ResolveRole(ctx); _db.LogAudit(who ?? "?", "yield.seasonality.save", string.Join(",", changed)); }
            catch (Exception ex) { Logger.Warning($"[Web 服务] 审计写入失败: {ex.Message}"); }
            var resp = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["restart"] = false,
                ["msg"] = "已保存并生效。",
                ["enabled"] = cfg.YieldSeasonalityEnabled,
                ["mode"] = cfg.YieldSeasonalityMode,
            };
            Respond(ctx, 200, "application/json; charset=utf-8",
                JsonSerializer.SerializeToUtf8Bytes(resp, JsonOpts));
        }
        catch (JsonException ex)
        {
            Logger.Warning($"[Web 服务] 季节性配置 JSON 非法: {ex.Message}");
            ctx.Response.StatusCode = 400;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 季节性配置保存失败: {ex.Message}");
            RespondText(ctx, 500, "seasonality config save failed");
        }
    }

    private async Task ServeLogin(HttpListenerContext ctx)
    {
        try
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            var name = doc.RootElement.TryGetProperty("name", out var pn) ? pn.GetString() ?? "" : "";
            var pwd = doc.RootElement.TryGetProperty("password", out var pp) ? pp.GetString() ?? "" : "";
            var u = _db.GetUserByName(name);
            if (u == null || !PasswordHasher.Verify(pwd, u.PwdHash))
            {
                RespondText(ctx, 401, "invalid credentials");
                return;
            }
            _db.LogAudit(u.Name, "login", "登录成功");
            Respond(ctx, 200, "application/json; charset=utf-8",
                JsonSerializer.SerializeToUtf8Bytes(new { ok = true, name = u.Name, role = u.Role, token = u.Token }, JsonOpts));
        }
        catch (JsonException) { RespondText(ctx, 400, "bad json"); }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 登录失败: {ex.Message}"); RespondText(ctx, 500, "login failed"); }
    }

    private void ServeStatus(HttpListenerContext ctx)
    {
        try
        {
            var machines = _mesh?.PeerStatuses ?? new List<PeerStatusDto>();
            int online = machines.Count(m => m.Online);
            long failTotal = _db.FailCountCached("");
            var today = DateTime.Now.ToString("yyyyMMdd");
            var yld = _db.QueryDailyStats(dateFromYmd: today, dateToYmd: today);
            int tTotal = yld.Sum(r => r.Total), tPass = yld.Sum(r => r.Pass);
            var (role, who) = ResolveRole(ctx);
            var uptime = _startedAt == default ? 0 : (long)(DateTime.Now - _startedAt).TotalSeconds;
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new
            {
                ok = true,
                version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "",
                uptime_sec = uptime,
                received = ReceivedCount,
                machines_total = machines.Count,
                machines_online = online,
                fail_total = failTotal,
                today_total = tTotal,
                today_pass = tPass,
                today_yield = tTotal > 0 ? Math.Round(tPass * 100.0 / tTotal, 2) : 100.0,
                auth = _token.Length > 0 ? "agg_token" : "open",
                role, who,
            }, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 状态查询失败: {ex.Message}"); RespondText(ctx, 500, "status failed"); }
    }

    private void ServeMetrics(HttpListenerContext ctx)
    {
        try
        {
            MeshPusher? pusher = _mesh?.Pusher;
            var recv = _mesh?.Receiver;
            var gossip = _mesh?.Gossiper;
            var uptime = _startedAt == default ? 0 : (long)(DateTime.Now - _startedAt).TotalSeconds;
            object? peers = _mesh?.PeerStatuses;
            object? perPeer = null;
            try { perPeer = pusher?.GetPerPeerMetrics(); } catch { }
            object? gossiper = null;
            try
            {
                if (gossip != null)
                    gossiper = new { interval_sec = gossip.CurrentIntervalSec, reason = gossip.AdaptiveReason, gossip_count = gossip.GossipCount, last_gap = gossip.LastGapCount, last_at = gossip.LastGossipAt };
            }
            catch { }
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new
            {
                ok = true,
                ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                uptime_sec = uptime,
                web = new { requests = ReceivedCount, rejected_503 = Rejected503Count },
                db = new { inserts = _db.InsertCount },
                receiver = recv == null ? null : new
                {
                    committed_batches = recv.CommittedBatches,
                    committed_rows = recv.CommittedRows,
                    received_fails = recv.ReceivedFails,
                    ignored_fails = recv.IgnoredFails,
                },
                pusher = pusher == null ? null : new
                {
                    queued = pusher.QueuedCount,
                    dropped = pusher.DroppedCount,
                    sent = pusher.SentCount,
                    failed = pusher.FailCount,
                    per_peer = perPeer,
                },
                gossiper,
                peers,
            }, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 指标查询失败: {ex.Message}"); RespondText(ctx, 500, "metrics failed"); }
    }

    private void ServeAudit(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "admin")) return;
        try
        {
            var limit = 200;
            if (int.TryParse(ctx.Request.QueryString["limit"], out var l) && l > 0) limit = l;
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(_db.QueryAudit(limit), JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 审计查询失败: {ex.Message}"); RespondText(ctx, 500, "audit failed"); }
    }

    private async Task ServeUsers(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "admin")) return;
        try
        {
            var (_, who) = ResolveRole(ctx);
            var method = ctx.Request.HttpMethod.ToUpperInvariant();
            if (method == "GET")
            {
                var list = _db.ListUsers().Select(u => new { u.Name, u.Role, u.Token, u.CreatedAt }).ToList();
                Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(list, JsonOpts));
                return;
            }
            if (method == "POST")
            {
                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                    body = await reader.ReadToEndAsync();
                using var doc = JsonDocument.Parse(body);
                var name = doc.RootElement.TryGetProperty("name", out var pn) ? pn.GetString() ?? "" : "";
                var pwd = doc.RootElement.TryGetProperty("password", out var pp) ? pp.GetString() ?? "" : "";
                var role = doc.RootElement.TryGetProperty("role", out var pr) ? pr.GetString() ?? "viewer" : "viewer";
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(pwd))
                { RespondText(ctx, 400, "name/password required"); return; }
                if (role != "viewer" && role != "engineer" && role != "admin")
                { RespondText(ctx, 400, "role must be viewer|engineer|admin"); return; }
                _db.UpsertUser(name, PasswordHasher.Hash(pwd), role);
                var created = _db.GetUserByName(name);
                _db.LogAudit(who ?? "?", "user.upsert", $"{name}/{role}");
                Respond(ctx, 200, "application/json; charset=utf-8",
                    JsonSerializer.SerializeToUtf8Bytes(new { ok = true, name, role, token = created?.Token ?? "" }, JsonOpts));
                return;
            }
            if (method == "DELETE")
            {
                var name = ctx.Request.QueryString["name"] ?? "";
                if (string.IsNullOrWhiteSpace(name)) { RespondText(ctx, 400, "name required"); return; }
                if (_db.DeleteUser(name))
                {
                    _db.LogAudit(who ?? "?", "user.delete", name);
                    RespondText(ctx, 200, "ok");
                }
                else RespondText(ctx, 404, "user not found");
                return;
            }
            RespondText(ctx, 405, "method not allowed");
        }
        catch (JsonException) { RespondText(ctx, 400, "bad json"); }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 用户管理失败: {ex.Message}"); RespondText(ctx, 500, "users failed"); }
    }

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

    private void ServeFailsCount(HttpListenerContext ctx)
    {
        try
        {
            var q = ctx.Request.QueryString;
            var machine = (q["machine"] ?? "").Trim();
            var keyword = (q["q"] ?? "").Trim();
            var count = _db.FailCountCached(machine.Length == 0 ? "" : machine, keyword.Length == 0 ? null : keyword);
            Respond(ctx, 200, "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes($"{{\"count\":{count}}}"));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 查询 FAIL 总数失败: {ex.Message}");
            RespondText(ctx, 500, "查询 FAIL 总数失败");
        }
    }

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
                    Esc(string.IsNullOrEmpty(r.Ts) ? r.IngestTs : r.Ts),
                    Esc(r.Machine), Esc(r.Model), Esc(r.Sn), Esc(r.TestDate),
                    Esc(r.FailReason), Esc(r.Tester), Esc(r.Result),
                }));
                sb.AppendLine();
            }
            ctx.Response.Headers["Content-Disposition"] =
                $"attachment; filename=\"fails_{DateTime.Now:yyyyMMdd_HHmmss}.csv\"";
            var enc = new UTF8Encoding(true);
            var preamble = enc.GetPreamble();
            var content = enc.GetBytes(sb.ToString());
            var bytes = new byte[preamble.Length + content.Length];
            preamble.CopyTo(bytes, 0);
            content.CopyTo(bytes, preamble.Length);
            try { var (_, who) = ResolveRole(ctx); _db.LogAudit(who ?? "?", "export.csv", $"{rows.Count} rows machine={machine}"); }
            catch (Exception ex) { Logger.Warning($"[Web 服务] 审计写入失败: {ex.Message}"); }
            Respond(ctx, 200, "text/csv; charset=utf-8", bytes);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 导出 CSV 失败: {ex.Message}");
            RespondText(ctx, 500, "导出 CSV 失败");
        }
    }

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
        if (status == 403)
        {
            if (TryServeRemoteXml(ctx, idStr)) return;
            RespondText(ctx, 403, ForbiddenText);
            return;
        }

        byte[] data;
        try { data = File.ReadAllBytes(full!); }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 读取报告文件失败 {full}: {ex.Message}");
            if (TryServeRemoteXml(ctx, idStr)) return;
            RespondText(ctx, 500, "读取文件失败");
            return;
        }

        ctx.Response.Headers["Content-Disposition"] =
            $"inline; filename*=UTF-8''{Uri.EscapeDataString(Path.GetFileName(full!))}";
        Respond(ctx, 200, ContentTypeFor(full!), data);
    }

    private bool TryServeRemoteXml(HttpListenerContext ctx, string? idStr)
    {
        if (_mesh == null || string.IsNullOrEmpty(idStr) || !long.TryParse(idStr, out var fid)) return false;
        try
        {
            var xml = _mesh.Receiver.FetchXmlForFail(fid);
            if (string.IsNullOrEmpty(xml)) return false;
            ctx.Response.Headers["Content-Disposition"] = "inline; filename*=UTF-8''report.xml";
            Respond(ctx, 200, "text/xml; charset=utf-8", Encoding.UTF8.GetBytes(xml));
            return true;
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 跨机拉取 XML 失败 id={fid}: {ex.Message}"); return false; }
    }

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
                    Size = 0,
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

    private sealed class ListEntry
    {
        public string Name { get; set; } = "";
        public bool IsDir { get; set; }
        public long Size { get; set; }
        public string Modified { get; set; } = "";
        public string Path { get; set; } = "";
    }

    private static (int limit, string? machine) ReadLimitMachine(HttpListenerContext ctx)
    {
        var q = ctx.Request.QueryString;
        var limit = DefaultLimit;
        if (int.TryParse(q["limit"], out var l) && l > 0) limit = Math.Min(l, MaxLimit);
        var machine = (q["machine"] ?? "").Trim();
        return (limit, machine.Length == 0 ? null : machine);
    }

    private void ServePush(HttpListenerContext ctx, string? forcedType)
    {
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException)
        {
            ctx.Response.StatusCode = 413;
            ctx.Response.KeepAlive = false;
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
            ctx.Response.StatusCode = 400;
            return;
        }

        try
        {
            if (_mesh != null)
            {
                if (string.Equals(type, "fail", StringComparison.OrdinalIgnoreCase)) _mesh.Receiver.HandleFail(body);
                else if (string.Equals(type, "heartbeat", StringComparison.OrdinalIgnoreCase)) _mesh.Receiver.HandleHeartbeat(body);
                else { ctx.Response.StatusCode = 400; return; }
            }
            else if (_watcher != null)
            {
                if (string.Equals(type, "fail", StringComparison.OrdinalIgnoreCase)) _watcher.IngestFail(body);
                else if (string.Equals(type, "heartbeat", StringComparison.OrdinalIgnoreCase)) _watcher.IngestHeartbeat(body);
                else { ctx.Response.StatusCode = 400; return; }
            }
            else { ctx.Response.StatusCode = 400; return; }
        }
        catch (AggIngestException ex)
        {
            Logger.Error($"[Web 服务] fail 入库失败 machine={ex.Machine} seq={ex.Seq}: {ex.Message}");
            ctx.Response.StatusCode = 500;
            return;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 推送回调异常（仍回 200）: {ex.Message}");
        }
        ctx.Response.StatusCode = 200;
    }

    private void ServeMeshPush(HttpListenerContext ctx, string forcedType)
    {
        ServePush(ctx, forcedType);
    }

    private void ServeMeshEvent(HttpListenerContext ctx)
    {
        if (_mesh == null) { ctx.Response.StatusCode = 404; return; }
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException)
        {
            ctx.Response.StatusCode = 413;
            ctx.Response.KeepAlive = false;
            return;
        }
        try { _mesh.TodoSync.HandleEvent(body); }
        catch (Exception ex) { Logger.Warning($"[Web 服务] mesh 事件处理失败: {ex.Message}"); }
        ctx.Response.StatusCode = 200;
    }

    private void ServeMeshSummary(HttpListenerContext ctx)
    {
        if (_mesh == null) { ctx.Response.StatusCode = 404; return; }
        var max = _db.MaxSeqPerMachine();
        var machines = max.Select(kv => new Dictionary<string, object> { ["machine"] = kv.Key, ["max_seq"] = kv.Value }).ToList();
        var payload = new Dictionary<string, object> { ["machines"] = machines };
        Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
    }

    private void ServeMeshFetch(HttpListenerContext ctx)
    {
        if (_mesh == null) { ctx.Response.StatusCode = 404; return; }
        var q = ctx.Request.QueryString;
        var machine = q["machine"] ?? "";
        long from = 0, to = 0;
        long.TryParse(q["from"], out from);
        long.TryParse(q["to"], out to);
        if (string.IsNullOrEmpty(machine) || to <= from) { ctx.Response.StatusCode = 400; return; }
        try
        {
            var rows = _db.QueryFailsByMachineSeqRange(machine, from, to);
            var events = rows.Select(r => SerializeFailRow(r)).ToList();
            var payload = new Dictionary<string, object> { ["events"] = events };
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] mesh fetch 失败: {ex.Message}");
            ctx.Response.StatusCode = 500;
        }
    }

    private void ServeMeshPeers(HttpListenerContext ctx)
    {
        if (_mesh == null) { ctx.Response.StatusCode = 404; return; }
        var payload = _mesh.PeerStatuses.Select(p => new Dictionary<string, object?>
        {
            ["machine"] = p.Machine, ["is_self"] = p.IsSelf, ["online"] = p.Online,
            ["last_heartbeat"] = p.LastHeartbeat, ["last_seq"] = p.LastSeq,
            ["queued"] = p.Queued, ["fail_count"] = p.FailCount,
        }).ToList();
        Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
    }

    private void ServeMeshXml(HttpListenerContext ctx)
    {
        if (_mesh == null) { ctx.Response.StatusCode = 404; return; }
        var idStr = ctx.Request.QueryString["id"] ?? "";
        if (!long.TryParse(idStr, out var id)) { ctx.Response.StatusCode = 400; return; }
        var xml = _mesh.Receiver.FetchXmlForFail(id);
        if (xml == null) { ctx.Response.StatusCode = 404; return; }
        Respond(ctx, 200, "application/xml; charset=utf-8", Encoding.UTF8.GetBytes(xml));
    }

    private void ServeTodos(HttpListenerContext ctx)
    {
        if (_mesh == null) { ctx.Response.StatusCode = 404; return; }
        var rows = _mesh.GetTodoSyncStates();
        var payload = rows.Select(r => new Dictionary<string, object?>
        {
            ["origin_machine"] = r.OriginMachine, ["todo_id"] = r.TodoId, ["owner"] = r.Owner,
            ["state"] = r.State, ["version"] = r.Version, ["updated_at"] = r.UpdatedAt,
        }).ToList();
        Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
    }

    #region Agent A: P4 源机检索（2026-08-28）— POST /api/mesh/query 扇出 + local 本机查询

    private Task ServeMeshQueryLocal(HttpListenerContext ctx)
    {
        if (_mesh == null)
        {
            RespondText(ctx, 503, "mesh not available");
            return Task.CompletedTask;
        }
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException) { ctx.Response.StatusCode = 413; ctx.Response.KeepAlive = false; return Task.CompletedTask; }
        try
        {
            var req = MeshQueryService.ParseRequest(body);
            req.Limit = Math.Clamp(req.Limit <= 0 ? 100 : req.Limit, 1, 2000);
            req.Offset = Math.Max(0, req.Offset);
            var items = MeshQueryService.QueryLocal(_mesh.LocalDb, _mesh.LocalMachine, req);
            var payload = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["machine"] = _mesh.LocalMachine,
                ["results"] = items,
                ["total"] = items.Count,
            };
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 本机查询失败: {ex.Message}");
            RespondText(ctx, 500, "local query failed");
        }
        return Task.CompletedTask;
    }

    private Task ServeMeshQuery(HttpListenerContext ctx)
    {
        if (_mesh == null)
        {
            RespondText(ctx, 503, "mesh not available");
            return Task.CompletedTask;
        }
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException) { ctx.Response.StatusCode = 413; ctx.Response.KeepAlive = false; return Task.CompletedTask; }
        MeshQueryService.QueryRequest req;
        try { req = MeshQueryService.ParseRequest(body); }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 查询请求解析失败: {ex.Message}");
            RespondText(ctx, 400, "bad json");
            return Task.CompletedTask;
        }
        req.Limit = Math.Clamp(req.Limit <= 0 ? 100 : req.Limit, 1, 2000);
        req.Offset = Math.Max(0, req.Offset);
        var cacheKey = MeshQueryService.CacheKey(req);
        if (MeshQueryService.TryGetCached(cacheKey, out var cachedJson))
        {
            var hitJson = cachedJson.Replace("\"cached\":false", "\"cached\":true");
            if (hitJson == cachedJson) ctx.Response.Headers["X-Cache"] = "HIT";
            Respond(ctx, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(hitJson));
            return Task.CompletedTask;
        }

        try
        {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var localItems = new List<MeshQueryService.QueryItem>();
        try { localItems = MeshQueryService.QueryLocal(_mesh.LocalDb, _mesh.LocalMachine, req); }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 本机查询异常: {ex.Message}"); }

        var peerUrls = _mesh.PeerUrls;
        var peerResults = new ConcurrentBag<List<MeshQueryService.QueryItem>>();
        var peerHits = new ConcurrentBag<MeshQueryService.PeerHit>();
        peerHits.Add(new MeshQueryService.PeerHit { Machine = _mesh.LocalMachine, Online = true, Count = localItems.Count });

        var fanoutTasks = peerUrls.Select(peer => Task.Run(() =>
        {
            var hit = new MeshQueryService.PeerHit { Machine = peer, Online = false, Count = 0 };
            try
            {
                var url = peer.TrimEnd('/') + "/api/mesh/query/local";
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var reqMsg = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                if (!string.IsNullOrEmpty(_token)) reqMsg.Headers.Add(MeshPusher.TokenHeader, _token);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
                using var resp = MeshPusher.SendStaticAsync(reqMsg, cts.Token).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                {
                    hit.Error = $"HTTP {(int)resp.StatusCode}";
                    peerHits.Add(hit);
                    return;
                }
                var txt = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(txt);
                var root = doc.RootElement;
                var arr = root.TryGetProperty("results", out var r) ? r : root;
                var list = new List<MeshQueryService.QueryItem>();
                if (arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        try
                        {
                            var it = new MeshQueryService.QueryItem
                            {
                                Machine = el.TryGetProperty("Machine", out var vm) ? vm.GetString() ?? peer : peer,
                                Id = el.TryGetProperty("Id", out var vi) && vi.TryGetInt64(out var idv) ? idv : 0,
                                Sn = el.TryGetProperty("Sn", out var vsn) ? vsn.GetString() ?? "" : "",
                                Model = el.TryGetProperty("Model", out var vmo) ? vmo.GetString() ?? "" : "",
                                Result = el.TryGetProperty("Result", out var vre) ? vre.GetString() ?? "" : "",
                                TestDate = el.TryGetProperty("TestDate", out var vtd) ? vtd.GetString() ?? "" : "",
                                FailReason = el.TryGetProperty("FailReason", out var vfr) ? vfr.GetString() ?? "" : "",
                                XmlPath = el.TryGetProperty("XmlPath", out var vxp) ? vxp.GetString() ?? "" : "",
                                FileSize = el.TryGetProperty("FileSize", out var vfs) && vfs.TryGetInt64(out var fsv) ? fsv : 0,
                                BatchTimestamp = el.TryGetProperty("BatchTimestamp", out var vbt) ? vbt.GetString() ?? "" : "",
                                Tester = el.TryGetProperty("Tester", out var vte) ? vte.GetString() ?? "" : "",
                            };
                            if (string.IsNullOrEmpty(it.Machine))
                                it.Machine = el.TryGetProperty("machine", out var vm2) ? vm2.GetString() ?? peer : peer;
                            list.Add(it);
                        }
                        catch { }
                    }
                }
                hit.Online = true;
                hit.Count = list.Count;
                if (list.Count > 0 && !string.IsNullOrEmpty(list[0].Machine) && list[0].Machine != peer)
                    hit.Machine = list[0].Machine;
                else hit.Machine = peer;
                peerHits.Add(hit);
                peerResults.Add(list);
            }
            catch (OperationCanceledException)
            {
                hit.Error = "timeout";
                peerHits.Add(hit);
            }
            catch (Exception ex)
            {
                hit.Error = ex.Message;
                peerHits.Add(hit);
            }
        })).ToArray();

        try { Task.WhenAll(fanoutTasks).GetAwaiter().GetResult(); } catch { }

        var merged = new List<MeshQueryService.QueryItem>(localItems);
        foreach (var lst in peerResults) merged.AddRange(lst);

        if (!string.IsNullOrWhiteSpace(req.Machine))
        {
            var want = req.Machine!.Trim();
            merged = merged.Where(x => string.Equals(x.Machine, want, StringComparison.OrdinalIgnoreCase)
                                    || x.Machine.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }
        if (!string.IsNullOrWhiteSpace(req.Sn))
        {
            var sn = req.Sn!.Trim();
            merged = merged.Where(x => (x.Sn ?? "").IndexOf(sn, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }
        if (!string.IsNullOrWhiteSpace(req.Model))
        {
            var mo = req.Model!.Trim();
            merged = merged.Where(x => (x.Model ?? "").IndexOf(mo, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }
        if (!string.IsNullOrWhiteSpace(req.Result) && !string.Equals(req.Result.Trim(), "ALL", StringComparison.OrdinalIgnoreCase))
        {
            var r = req.Result!.Trim().ToUpperInvariant();
            merged = merged.Where(x => string.Equals(x.Result ?? "", r, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        merged.Sort((a, b) =>
        {
            var c = string.Compare(b.TestDate ?? "", a.TestDate ?? "", StringComparison.Ordinal);
            if (c != 0) return c;
            return b.Id.CompareTo(a.Id);
        });

        var total = merged.Count;
        var paged = merged.Skip(req.Offset).Take(req.Limit).ToList();

        var respObj = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["cached"] = false,
            ["total"] = total,
            ["results"] = paged,
            ["peers"] = peerHits.ToList(),
            ["elapsed_ms"] = sw.ElapsedMilliseconds,
        };
        string json;
        try { json = JsonSerializer.Serialize(respObj, JsonOpts); }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 聚合查询序列化失败: {ex.Message}");
            RespondText(ctx, 500, "query serialize failed");
            return Task.CompletedTask;
        }
        MeshQueryService.PutCached(cacheKey, json);
        Respond(ctx, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 聚合查询异常: {ex.Message}");
            RespondText(ctx, 500, "query failed: " + ex.Message);
        }
        return Task.CompletedTask;
    }

    #endregion

    #region Agent B: P5 维修/待办全功能（2026-08-28）— 维修看板服务端化

    private void ServeMaintenanceList(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var q = ctx.Request.QueryString;
            var status = (q["status"] ?? "").Trim();
            if (!string.IsNullOrEmpty(status)) status = MaintenanceMeta.Normalize(status);
            var limit = 500;
            if (int.TryParse(q["limit"], out var l) && l > 0) limit = Math.Min(l, 2000);
            var kw = (q["q"] ?? q["keyword"] ?? "").Trim();
            List<MaintenanceRecord> rows;
            if (string.IsNullOrEmpty(status))
                rows = _db.ListMaintenance("", limit + 200);
            else
                rows = _db.ListMaintenance(status, limit + 200);
            if (status == "open")
            {
                var inv = _db.ListMaintenance("investigating", 200);
                if (inv.Count > 0) rows = rows.Concat(inv).OrderByDescending(m => string.IsNullOrEmpty(m.UpdatedAt) ? m.CreatedAt : m.UpdatedAt).ThenByDescending(m => m.Id).ToList();
            }
            else if (status == "resolved")
            {
                var closed = _db.ListMaintenance("closed", 200);
                if (closed.Count > 0) rows = rows.Concat(closed).OrderByDescending(m => string.IsNullOrEmpty(m.UpdatedAt) ? m.CreatedAt : m.UpdatedAt).ThenByDescending(m => m.Id).ToList();
            }
            if (!string.IsNullOrEmpty(kw))
            {
                var lower = kw.ToLowerInvariant();
                rows = rows.Where(r =>
                    (r.FailItem ?? "").ToLowerInvariant().Contains(lower) ||
                    (r.FailReason ?? "").ToLowerInvariant().Contains(lower) ||
                    (r.Resolver ?? "").ToLowerInvariant().Contains(lower) ||
                    (r.Resolution ?? "").ToLowerInvariant().Contains(lower) ||
                    (r.Notes ?? "").ToLowerInvariant().Contains(lower)).ToList();
            }
            if (rows.Count > limit) rows = rows.Take(limit).ToList();
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(rows, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 查询维修记录失败: {ex.Message}"); RespondText(ctx, 500, "query maintenance failed"); }
    }

    private void ServeMaintenanceCounts(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var raw = _db.CountMaintenanceByStatus();
            var norm = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in raw)
            {
                var k = MaintenanceMeta.Normalize(kv.Key);
                norm[k] = norm.GetValueOrDefault(k) + kv.Value;
            }
            foreach (var def in MaintenanceMeta.Statuses)
                if (!norm.ContainsKey(def.Key)) norm[def.Key] = 0;
            var total = norm.Values.Sum();
            var payload = new Dictionary<string, object?>
            {
                ["counts"] = norm,
                ["total"] = total,
                ["unknown"] = norm.GetValueOrDefault("unknown"),
                ["open"] = norm.GetValueOrDefault("open"),
                ["in_progress"] = norm.GetValueOrDefault("in_progress"),
                ["resolved"] = norm.GetValueOrDefault("resolved"),
            };
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 维修计数失败: {ex.Message}"); RespondText(ctx, 500, "counts failed"); }
    }

    private Task ServeMaintenanceCreate(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "engineer")) return Task.CompletedTask;
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException) { ctx.Response.StatusCode = 413; ctx.Response.KeepAlive = false; return Task.CompletedTask; }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var (_, who) = ResolveRole(ctx);
            if (root.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
            {
                var items = new List<string>();
                foreach (var el in itemsEl.EnumerateArray())
                {
                    var s = el.GetString() ?? "";
                    s = s.Trim();
                    if (s.Length > 0 && !items.Contains(s, StringComparer.OrdinalIgnoreCase)) items.Add(s);
                }
                if (items.Count == 0) { RespondText(ctx, 400, "items empty"); return Task.CompletedTask; }
                bool merge = root.TryGetProperty("merge", out var m) && m.ValueKind == JsonValueKind.True;
                var rec = ParseMaintenanceRecord(root, who);
                var results = new List<int>();
                if (merge)
                {
                    rec.FailItem = string.Join(" / ", items);
                    var id = _db.CreateMaintenance(rec);
                    results.Add(id);
                    foreach (var n in ResolverUtil.Split(rec.Resolver)) try { _db.AddResolver(n); } catch { }
                    if (!string.IsNullOrEmpty(rec.Status)) NotifyMaintenancePush(rec, "", rec.Status);
                    _db.LogAudit(who ?? "?", "maintenance.create.merge", rec.FailItem);
                }
                else
                {
                    foreach (var it in items)
                    {
                        var one = rec.Clone();
                        one.Id = 0;
                        one.FailItem = it;
                        var id = _db.CreateMaintenance(one);
                        results.Add(id);
                        if (!string.IsNullOrEmpty(one.Status)) NotifyMaintenancePush(one, "", one.Status);
                    }
                    foreach (var n in ResolverUtil.Split(rec.Resolver)) try { _db.AddResolver(n); } catch { }
                    _db.LogAudit(who ?? "?", "maintenance.create.batch", string.Join(";", items));
                }
                Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, ids = results }, JsonOpts));
                return Task.CompletedTask;
            }
            var single = ParseMaintenanceRecord(root, who);
            if (string.IsNullOrWhiteSpace(single.FailItem)) { RespondText(ctx, 400, "fail_item required"); return Task.CompletedTask; }
            single.Status = MaintenanceMeta.Normalize(single.Status);
            single.Severity = string.IsNullOrEmpty(single.Severity) ? MaintenanceMeta.DefaultSeverity : single.Severity;
            foreach (var n in ResolverUtil.Split(single.Resolver)) try { _db.AddResolver(n); } catch { }
            var sid = _db.CreateMaintenance(single);
            single.Id = sid;
            NotifyMaintenancePush(single, "", single.Status);
            _db.LogAudit(who ?? "?", "maintenance.create", $"#{sid} {single.FailItem}");
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, id = sid }, JsonOpts));
        }
        catch (JsonException) { RespondText(ctx, 400, "bad json"); }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 新建维修记录失败: {ex.Message}"); RespondText(ctx, 500, "create failed: " + ex.Message); }
        return Task.CompletedTask;
    }

    private MaintenanceRecord ParseMaintenanceRecord(JsonElement root, string? who)
    {
        string G(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var rec = new MaintenanceRecord
        {
            StationId = G("station_id") != "" ? G("station_id") : G("stationId"),
            EquipmentModel = G("equipment_model") != "" ? G("equipment_model") : G("equipmentModel"),
            EquipmentSn = G("equipment_sn") != "" ? G("equipment_sn") : G("equipmentSn"),
            FailItem = G("fail_item") != "" ? G("fail_item") : G("failItem"),
            FailReason = G("fail_reason") != "" ? G("fail_reason") : G("failReason"),
            Severity = G("severity"),
            Status = G("status"),
            Resolver = G("resolver"),
            Resolution = G("resolution") != "" ? G("resolution") : G("measures"),
            Notes = G("notes"),
            CreatedAt = G("created_at") != "" ? G("created_at") : (G("createdAt") != "" ? G("createdAt") : G("date")),
        };
        if (string.IsNullOrEmpty(rec.StationId) && root.TryGetProperty("machine", out var vm) && vm.ValueKind == JsonValueKind.String) rec.StationId = vm.GetString() ?? "";
        rec.Resolver = ResolverUtil.Normalize(rec.Resolver);
        if (!string.IsNullOrEmpty(rec.Severity)) rec.Severity = MaintenanceMeta.SeverityKeyOf(MaintenanceMeta.SeverityZhOf(rec.Severity)) == rec.Severity ? rec.Severity : MaintenanceMeta.SeverityKeyOf(rec.Severity);
        if (rec.Severity != "critical" && rec.Severity != "major" && rec.Severity != "minor")
        {
            var zh = MaintenanceMeta.SeverityZhOf(rec.Severity);
            rec.Severity = MaintenanceMeta.SeverityKeyOf(string.IsNullOrEmpty(zh) ? "一般" : zh);
        }
        if (string.IsNullOrEmpty(rec.Status)) rec.Status = MaintenanceMeta.DefaultStatus;
        rec.Status = MaintenanceMeta.Normalize(rec.Status);
        return rec;
    }

    private Task ServeMaintenanceUpdate(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "engineer")) return Task.CompletedTask;
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException) { ctx.Response.StatusCode = 413; ctx.Response.KeepAlive = false; return Task.CompletedTask; }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            int id = 0;
            if (root.TryGetProperty("id", out var pid) && pid.ValueKind == JsonValueKind.Number) id = pid.GetInt32();
            else if (root.TryGetProperty("Id", out var pid2) && pid2.ValueKind == JsonValueKind.Number) id = pid2.GetInt32();
            if (id <= 0) { RespondText(ctx, 400, "id required"); return Task.CompletedTask; }
            var existing = _db.GetMaintenance(id);
            if (existing == null) { RespondText(ctx, 404, "not found"); return Task.CompletedTask; }
            var fromStatus = existing.Status;
            string G(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string GetAny(params string[] keys) { foreach (var k in keys) { var v = G(k); if (v != "") return v; } return ""; }
            var updated = existing.Clone();
            var fi = GetAny("fail_item", "failItem"); if (fi != "") updated.FailItem = fi;
            var fr = GetAny("fail_reason", "failReason"); if (root.TryGetProperty("fail_reason", out _) || root.TryGetProperty("failReason", out _)) updated.FailReason = fr;
            var sv = GetAny("severity"); if (sv != "") updated.Severity = sv;
            var st = GetAny("status"); if (st != "") updated.Status = st;
            var rs = GetAny("resolver"); if (root.TryGetProperty("resolver", out _)) updated.Resolver = ResolverUtil.Normalize(rs);
            var reso = GetAny("resolution", "measures"); if (root.TryGetProperty("resolution", out _) || root.TryGetProperty("measures", out _)) updated.Resolution = reso;
            var nt = GetAny("notes"); if (root.TryGetProperty("notes", out _)) updated.Notes = nt;
            var em = GetAny("equipment_model", "equipmentModel"); if (em != "" || root.TryGetProperty("equipment_model", out _) || root.TryGetProperty("equipmentModel", out _)) updated.EquipmentModel = em;
            var es = GetAny("equipment_sn", "equipmentSn"); if (es != "" || root.TryGetProperty("equipment_sn", out _) || root.TryGetProperty("equipmentSn", out _)) updated.EquipmentSn = es;
            var ca = GetAny("created_at", "createdAt", "date"); if (ca != "") updated.CreatedAt = ca;
            if (!string.IsNullOrEmpty(updated.Severity) && updated.Severity != "critical" && updated.Severity != "major" && updated.Severity != "minor")
                updated.Severity = MaintenanceMeta.SeverityKeyOf(MaintenanceMeta.SeverityZhOf(updated.Severity));
            updated.Status = MaintenanceMeta.Normalize(updated.Status);
            foreach (var n in ResolverUtil.Split(updated.Resolver)) try { _db.AddResolver(n); } catch { }
            if (!_db.UpdateMaintenance(updated)) { RespondText(ctx, 500, "update failed"); return Task.CompletedTask; }
            var (_, who) = ResolveRole(ctx);
            _db.LogAudit(who ?? "?", "maintenance.update", $"#{id} {fromStatus}->{updated.Status}");
            if (!string.Equals(fromStatus, updated.Status, StringComparison.OrdinalIgnoreCase))
                NotifyMaintenancePush(updated, fromStatus, updated.Status);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, id }, JsonOpts));
        }
        catch (JsonException) { RespondText(ctx, 400, "bad json"); }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 更新维修失败: {ex.Message}"); RespondText(ctx, 500, "update failed"); }
        return Task.CompletedTask;
    }

    private void ServeMaintenanceDelete(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "engineer")) return;
        try
        {
            var q = ctx.Request.QueryString;
            int id = 0;
            int.TryParse(q["id"], out id);
            if (id <= 0)
            {
                try
                {
                    var body = ReadBody(ctx.Request);
                    if (!string.IsNullOrEmpty(body))
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("id", out var v) && v.TryGetInt32(out var bid)) id = bid;
                    }
                }
                catch { }
            }
            if (id <= 0) { RespondText(ctx, 400, "id required"); return; }
            var rec = _db.GetMaintenance((int)id);
            if (rec == null) { RespondText(ctx, 404, "not found"); return; }
            if (!_db.DeleteMaintenance(id)) { RespondText(ctx, 500, "delete failed"); return; }
            var (_, who) = ResolveRole(ctx);
            _db.LogAudit(who ?? "?", "maintenance.delete", $"#{id} {rec.FailItem}");
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true }, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 删除维修失败: {ex.Message}"); RespondText(ctx, 500, "delete failed"); }
    }

    private void NotifyMaintenancePush(MaintenanceRecord rec, string from, string to)
    {
        try
        {
            var url = AppConfig.Instance.AggWebhookUrl;
            if (string.IsNullOrWhiteSpace(url)) url = AppConfig.Instance.WebhookUrl;
            if (string.IsNullOrWhiteSpace(url)) return;
            var r = rec;
            var f = from; var t = to;
            Task.Run(() => FeishuNotifier.SendStatusChangeAlert(url, r, f, t));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 飞书推送异常: {ex.Message}"); }
    }

    private void ServeTodoList(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var q = ctx.Request.QueryString;
            DateTime? from = null, to = null;
            if (!string.IsNullOrWhiteSpace(q["from"])) { if (DateTime.TryParseExact(q["from"]!.Trim(), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var fd)) from = fd; else if (DateTime.TryParse(q["from"]!.Trim(), out var fd2)) from = fd2.Date; }
            if (!string.IsNullOrWhiteSpace(q["to"])) { if (DateTime.TryParseExact(q["to"]!.Trim(), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var td)) to = td; else if (DateTime.TryParse(q["to"]!.Trim(), out var td2)) to = td2.Date; }
            int limit = 300;
            if (int.TryParse(q["limit"], out var l) && l > 0) limit = Math.Min(l, 1000);
            var hasRange = !string.IsNullOrWhiteSpace(q["from"]) || !string.IsNullOrWhiteSpace(q["to"]);
            if (!hasRange) { from = null; to = null; }
            try { _db.SyncTodoItems(AppConfig.Instance.TodoScanDays); } catch { }
            var list = _db.ListTodoView(from, to, limit);
            var payload = list.Select(t => new
            {
                t.Id, t.GroupKey, t.Title, t.StationId, t.Model,
                Variants = t.Variants,
                t.VariantCount, t.TotalCount, t.RangeCount, SortCount = t.SortCount,
                PriorityZh = t.PriorityZh,
                Priority = TodoGrouping.PriorityZhOf(t.SortCount),
                t.FirstSeen, t.LastSeen, t.RangeFirstSeen, t.RangeLastSeen, t.State,
            });
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 查询待办失败: {ex.Message}"); RespondText(ctx, 500, "query todos failed"); }
    }

    private async Task ServeTodoCreate(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "engineer")) return;
        RespondText(ctx, 405, "todos are auto-synced from fails; use POST /api/todos/ack to confirm");
        await Task.CompletedTask;
    }

    private Task ServeTodoAck(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "engineer")) return Task.CompletedTask;
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException) { ctx.Response.StatusCode = 413; ctx.Response.KeepAlive = false; return Task.CompletedTask; }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            int todoId = 0;
            if (root.TryGetProperty("todoId", out var tv) && tv.TryGetInt32(out var tid)) todoId = tid;
            else if (root.TryGetProperty("todo_id", out var tv2) && tv2.TryGetInt32(out var tid2)) todoId = tid2;
            else if (root.TryGetProperty("id", out var tv3) && tv3.TryGetInt32(out var tid3)) todoId = tid3;
            if (todoId <= 0) { RespondText(ctx, 400, "todoId required"); return Task.CompletedTask; }
            var todo = _db.GetTodoItem(todoId);
            if (todo == null) { RespondText(ctx, 404, "todo not found"); return Task.CompletedTask; }
            string G(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var rec = new MaintenanceRecord
            {
                StationId = string.IsNullOrEmpty(G("station_id")) ? (string.IsNullOrEmpty(G("stationId")) ? todo.StationId : G("stationId")) : G("station_id"),
                FailItem = string.IsNullOrEmpty(G("fail_item")) ? (string.IsNullOrEmpty(G("failItem")) ? todo.Title : G("failItem")) : G("fail_item"),
                FailReason = G("fail_reason") != "" ? G("fail_reason") : G("failReason"),
                Severity = G("severity"),
                Status = G("status"),
                Resolver = ResolverUtil.Normalize(G("resolver")),
                Resolution = G("resolution") != "" ? G("resolution") : G("measures"),
                Notes = G("notes"),
                CreatedAt = G("created_at") != "" ? G("created_at") : G("createdAt"),
            };
            if (string.IsNullOrEmpty(rec.Status)) rec.Status = MaintenanceMeta.DefaultStatus;
            rec.Status = MaintenanceMeta.Normalize(rec.Status);
            if (string.IsNullOrEmpty(rec.Severity)) rec.Severity = todo.SortCount >= TodoGrouping.HighThreshold ? "critical" : "major";
            foreach (var n in ResolverUtil.Split(rec.Resolver)) try { _db.AddResolver(n); } catch { }
            var mid = _db.AcknowledgeTodo(todoId, rec);
            var (_, who) = ResolveRole(ctx);
            _db.LogAudit(who ?? "?", "todo.ack", $"todo#{todoId}->#{mid} {rec.FailItem}");
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, maintenance_id = mid }, JsonOpts));
        }
        catch (JsonException) { RespondText(ctx, 400, "bad json"); }
        catch (InvalidOperationException ex) { RespondText(ctx, 404, ex.Message); }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 确认待办失败: {ex.Message}"); RespondText(ctx, 500, "ack failed"); }
        return Task.CompletedTask;
    }

    private void ServeTodoDelete(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "engineer")) return;
        try
        {
            var q = ctx.Request.QueryString;
            int id = 0;
            int.TryParse(q["id"], out id);
            if (id <= 0) int.TryParse(q["todoId"], out id);
            if (id <= 0)
            {
                try { var body = ReadBody(ctx.Request); if (!string.IsNullOrEmpty(body)) { using var doc = JsonDocument.Parse(body); if (doc.RootElement.TryGetProperty("id", out var v) && v.TryGetInt32(out var bid)) id = bid; } } catch { }
            }
            if (id <= 0) { RespondText(ctx, 400, "id required"); return; }
            if (!_db.DeleteTodo(id)) { RespondText(ctx, 404, "not found"); return; }
            var (_, who) = ResolveRole(ctx);
            _db.LogAudit(who ?? "?", "todo.delete", $"#{id}");
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true }, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 删除待办失败: {ex.Message}"); RespondText(ctx, 500, "delete failed"); }
    }

    private void ServeResolvers(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var list = _db.ListResolvers();
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(list, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 查询维修人失败: {ex.Message}"); RespondText(ctx, 500, "resolver query failed"); }
    }

    private Task ServeResolversCreate(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "engineer")) return Task.CompletedTask;
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException) { ctx.Response.StatusCode = 413; ctx.Response.KeepAlive = false; return Task.CompletedTask; }
        try
        {
            string name = "";
            try { using var doc = JsonDocument.Parse(body); var r = doc.RootElement; if (r.TryGetProperty("name", out var v) && v.ValueKind == JsonValueKind.String) name = v.GetString() ?? ""; else if (r.ValueKind == JsonValueKind.String) name = r.GetString() ?? ""; } catch { name = body.Trim().Trim('"'); }
            if (string.IsNullOrWhiteSpace(name)) { RespondText(ctx, 400, "name required"); return Task.CompletedTask; }
            if (name.Length > 32) { RespondText(ctx, 400, "name too long"); return Task.CompletedTask; }
            var ok = _db.AddResolver(name.Trim());
            var (_, who) = ResolveRole(ctx);
            _db.LogAudit(who ?? "?", "resolver.add", name);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok, name }, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 新增维修人失败: {ex.Message}"); RespondText(ctx, 500, "add failed"); }
        return Task.CompletedTask;
    }

    private void ServeResolversDelete(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "engineer")) return;
        try
        {
            var q = ctx.Request.QueryString;
            var name = (q["name"] ?? "").Trim();
            if (string.IsNullOrEmpty(name))
            {
                try { var body = ReadBody(ctx.Request); if (!string.IsNullOrEmpty(body)) { using var doc = JsonDocument.Parse(body); if (doc.RootElement.TryGetProperty("name", out var v) && v.ValueKind == JsonValueKind.String) name = v.GetString() ?? ""; } } catch { }
            }
            if (string.IsNullOrWhiteSpace(name)) { RespondText(ctx, 400, "name required"); return; }
            var used = _db.CountRecordsByResolver(name);
            var ok = _db.DeleteResolver(name);
            if (!ok) { RespondText(ctx, 404, "not found"); return; }
            var (_, who) = ResolveRole(ctx);
            _db.LogAudit(who ?? "?", "resolver.delete", $"{name} used={used}");
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, used }, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 删除维修人失败: {ex.Message}"); RespondText(ctx, 500, "delete failed"); }
    }

    private Task ServeResolversRename(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "engineer")) return Task.CompletedTask;
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException) { ctx.Response.StatusCode = 413; ctx.Response.KeepAlive = false; return Task.CompletedTask; }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var oldName = root.TryGetProperty("oldName", out var o) ? o.GetString() ?? "" : (root.TryGetProperty("old_name", out var o2) ? o2.GetString() ?? "" : "");
            var newName = root.TryGetProperty("newName", out var n) ? n.GetString() ?? "" : (root.TryGetProperty("new_name", out var n2) ? n2.GetString() ?? "" : "");
            bool sync = false;
            if (root.TryGetProperty("sync", out var s)) sync = s.ValueKind == JsonValueKind.True;
            else if (root.TryGetProperty("syncRecords", out var s2)) sync = s2.ValueKind == JsonValueKind.True;
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) { RespondText(ctx, 400, "oldName/newName required"); return Task.CompletedTask; }
            if (newName.Trim().Length > 32) { RespondText(ctx, 400, "newName too long"); return Task.CompletedTask; }
            var synced = _db.RenameResolver(oldName.Trim(), newName.Trim(), sync);
            var (_, who) = ResolveRole(ctx);
            _db.LogAudit(who ?? "?", "resolver.rename", $"{oldName}->{newName} sync={sync} count={synced}");
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, synced }, JsonOpts));
        }
        catch (JsonException) { RespondText(ctx, 400, "bad json"); }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 改名维修人失败: {ex.Message}"); RespondText(ctx, 500, "rename failed"); }
        return Task.CompletedTask;
    }

    private void ServeExportMaintenance(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var q = ctx.Request.QueryString;
            var fmt = (q["format"] ?? q["type"] ?? "xlsx").Trim().ToLowerInvariant();
            var status = (q["status"] ?? "").Trim();
            if (!string.IsNullOrEmpty(status)) status = MaintenanceMeta.Normalize(status);
            var kw = (q["q"] ?? "").Trim();
            var list = string.IsNullOrEmpty(status) ? _db.ListMaintenance("", 2000) : _db.ListMaintenance(status, 2000);
            if (!string.IsNullOrEmpty(kw))
            {
                var lower = kw.ToLowerInvariant();
                list = list.Where(r => (r.FailItem ?? "").ToLowerInvariant().Contains(lower) || (r.FailReason ?? "").ToLowerInvariant().Contains(lower) || (r.Resolver ?? "").ToLowerInvariant().Contains(lower)).ToList();
            }
            var (_, who) = ResolveRole(ctx);
            _db.LogAudit(who ?? "?", "export.maintenance", $"{list.Count} rows format={fmt} status={status}");
            if (fmt == "csv")
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"maint_{Guid.NewGuid():N}.csv");
                try
                {
                    MaintenanceExporter.ExportCsv(tmp, list);
                    var data = File.ReadAllBytes(tmp);
                    ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"maintenance_{DateTime.Now:yyyyMMdd_HHmmss}.csv\"";
                    Respond(ctx, 200, "text/csv; charset=utf-8", data);
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
            else
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"maint_{Guid.NewGuid():N}.xlsx");
                try
                {
                    MaintenanceExporter.ExportXlsx(tmp, list);
                    var data = File.ReadAllBytes(tmp);
                    ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"maintenance_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx\"";
                    Respond(ctx, 200, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", data);
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 导出维修记录失败: {ex.Message}"); RespondText(ctx, 500, "export failed"); }
    }

    #endregion

    #region Agent C: P6 设备监控 + P7 数据拉取雏形（2026-08-28）— 路由实现

    private readonly object _deviceCacheLock = new();
    private DateTime _deviceCacheAt = DateTime.MinValue;
    private byte[]? _deviceCacheBytes;

    private void InvalidateDeviceCache() { lock (_deviceCacheLock) { _deviceCacheAt = DateTime.MinValue; _deviceCacheBytes = null; } }

    private void ServeMeshInfo(HttpListenerContext ctx)
    {
        if (_mesh == null) { ctx.Response.StatusCode = 404; return; }
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException) { ctx.Response.StatusCode = 413; ctx.Response.KeepAlive = false; return; }
        try { _mesh.Receiver.HandleInfo(body); InvalidateDeviceCache(); }
        catch (Exception ex) { Logger.Warning($"[Web 服务] HandleInfo 失败: {ex.Message}"); }
        ctx.Response.StatusCode = 200;
    }

    private void ServeMeshFctIni(HttpListenerContext ctx)
    {
        if (_mesh == null) { ctx.Response.StatusCode = 404; return; }
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException) { ctx.Response.StatusCode = 413; ctx.Response.KeepAlive = false; return; }
        try { _mesh.Receiver.HandleFctIni(body); }
        catch (Exception ex) { Logger.Warning($"[Web 服务] HandleFctIni 失败: {ex.Message}"); }
        ctx.Response.StatusCode = 200;
    }

    private void ServeDevices(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            byte[]? cached = null;
            lock (_deviceCacheLock)
            {
                if (_deviceCacheBytes != null && (DateTime.UtcNow - _deviceCacheAt).TotalMilliseconds < 5000)
                    cached = _deviceCacheBytes;
            }
            if (cached != null)
            {
                Respond(ctx, 200, "application/json; charset=utf-8", cached);
                return;
            }
            var infos = _db.ListDeviceInfos();
            var fcts = _db.ListDeviceFcts().ToDictionary(x => x.Machine, StringComparer.OrdinalIgnoreCase);
            var peers = _mesh?.PeerStatuses ?? new List<PeerStatusDto>();
            var onlineMap = peers.ToDictionary(p => p.Machine, p => p.Online, StringComparer.OrdinalIgnoreCase);
            foreach (var p in peers)
            {
                if (!infos.Any(x => string.Equals(x.Machine, p.Machine, StringComparison.OrdinalIgnoreCase)))
                {
                    infos.Add(new DeviceInfoRow { Machine = p.Machine, LastSeen = p.LastHeartbeat ?? "", Online = p.Online });
                }
            }
            var dto = infos.Select(d =>
            {
                var on = onlineMap.TryGetValue(d.Machine, out var ov) ? ov : IsDeviceOnline(d.LastSeen);
                var fct = fcts.TryGetValue(d.Machine, out var fv) ? fv : null;
                return new
                {
                    machine = d.Machine,
                    hostname = d.Hostname,
                    os = d.Os,
                    os_version = d.OsVersion,
                    ip = d.Ip,
                    mac = d.Mac,
                    cpu_model = d.CpuModel,
                    cpu_cores = d.CpuCores,
                    cpu_usage = d.CpuUsage,
                    mem_total_mb = d.MemTotalMb,
                    mem_used_mb = d.MemUsedMb,
                    disk_total_gb = d.DiskTotalGb,
                    disk_free_gb = d.DiskFreeGb,
                    uptime_sec = d.UptimeSec,
                    argus_version = d.ArgusVersion,
                    last_seen = d.LastSeen,
                    updated_at = d.UpdatedAt,
                    online = on,
                    fct_models = fct?.Models ?? new List<string>(),
                    fct_found = fct?.Found ?? false,
                };
            }).OrderBy(x => x.machine, StringComparer.OrdinalIgnoreCase).ToList();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOpts);
            lock (_deviceCacheLock) { _deviceCacheBytes = bytes; _deviceCacheAt = DateTime.UtcNow; }
            Respond(ctx, 200, "application/json; charset=utf-8", bytes);
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] 查询 devices 失败: {ex.Message}"); RespondText(ctx, 500, "devices failed"); }
    }

    private bool IsDeviceOnline(string lastSeen)
    {
        if (string.IsNullOrEmpty(lastSeen)) return false;
        if (!DateTime.TryParse(lastSeen, out var dt)) return false;
        return (DateTime.Now - dt).TotalSeconds <= 90;
    }

    private void ServeDeviceWildcard(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        var raw = ctx.Request.Url?.AbsolutePath ?? "";
        var path = raw.Length > 1 ? raw.TrimEnd('/') : raw;
        var rel = path.Substring("/api/devices".Length).Trim('/');
        if (string.IsNullOrEmpty(rel)) { ServeDevices(ctx); return; }
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var machine = parts[0];
        if (parts.Length == 1)
        {
            try
            {
                var info = _db.GetDeviceInfo(machine);
                if (info == null)
                {
                    var peers = _mesh?.PeerStatuses ?? new List<PeerStatusDto>();
                    var p = peers.FirstOrDefault(x => string.Equals(x.Machine, machine, StringComparison.OrdinalIgnoreCase));
                    if (p == null) { RespondText(ctx, 404, "device not found"); return; }
                    info = new DeviceInfoRow { Machine = p.Machine, LastSeen = p.LastHeartbeat ?? "", Online = p.Online };
                }
                else
                {
                    var peers = _mesh?.PeerStatuses ?? new List<PeerStatusDto>();
                    var om = peers.FirstOrDefault(x => string.Equals(x.Machine, machine, StringComparison.OrdinalIgnoreCase));
                    info.Online = om != null ? om.Online : IsDeviceOnline(info.LastSeen);
                }
                var fct = _db.GetDeviceFct(machine);
                var payload = new
                {
                    info,
                    fct = fct == null ? null : new
                    {
                        fct.Machine, fct.IniPath, fct.Found, fct.Error, fct.Models, fw_versions = fct.FwVersions.Select(x => new { label = x.Label, version = x.Version }),
                        devices = fct.Devices.Select(d => new { name = d.Name, port = d.Port, type = d.Type, online = d.Online }),
                        a2l_files = fct.A2lFiles.Select(x => new { label = x.Label, file = x.File }),
                        fct.LastSeen, fct.UpdatedAt,
                    },
                    samples_count = _db.QueryDeviceSamples(machine, 1).Count,
                };
                Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
            }
            catch (Exception ex) { Logger.Warning($"[Web 服务] device detail 失败 {machine}: {ex.Message}"); RespondText(ctx, 500, "device detail failed"); }
            return;
        }
        if (parts.Length == 2 && string.Equals(parts[1], "samples", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var q = ctx.Request.QueryString;
                int limit = 200;
                if (int.TryParse(q["limit"], out var l) && l > 0) limit = Math.Min(l, 2000);
                var from = q["from"];
                var to = q["to"];
                var samples = _db.QueryDeviceSamples(machine, limit, from, to);
                Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(samples, JsonOpts));
            }
            catch (Exception ex) { Logger.Warning($"[Web 服务] device samples 失败 {machine}: {ex.Message}"); RespondText(ctx, 500, "samples failed"); }
            return;
        }
        if (parts.Length == 2 && string.Equals(parts[1], "fct", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var fct = _db.GetDeviceFct(machine);
                if (fct == null) { RespondText(ctx, 404, "fct not found"); return; }
                Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new
                {
                    fct.Machine, fct.IniPath, fct.Found, fct.Error, fct.Models,
                    fw_versions = fct.FwVersions.Select(x => new { label = x.Label, version = x.Version }),
                    devices = fct.Devices.Select(d => new { name = d.Name, port = d.Port, type = d.Type, online = d.Online }),
                    a2l_files = fct.A2lFiles.Select(x => new { label = x.Label, file = x.File }),
                    fct.LastSeen, fct.UpdatedAt,
                }, JsonOpts));
            }
            catch (Exception ex) { Logger.Warning($"[Web 服务] device fct 失败 {machine}: {ex.Message}"); RespondText(ctx, 500, "fct failed"); }
            return;
        }
        RespondText(ctx, 404, "not found");
    }

    private async Task ServeFetchCreate(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException) { ctx.Response.StatusCode = 413; ctx.Response.KeepAlive = false; return; }
        try
        {
            var job = FetcherService.CreateExport(_db, body);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, job_id = job.Id, status = job.Status, progress = job.Progress }, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] fetch create 失败: {ex.Message}"); RespondText(ctx, 500, "fetch failed"); }
        await Task.CompletedTask;
    }

    private void ServeFetchJobs(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var jobs = FetcherService.List(20);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(jobs.Select(j => new { id = j.Id, status = j.Status, total = j.Total, progress = j.Progress, fileName = j.FileName, fileSize = j.FileSize, format = j.Format, created_at = j.CreatedAt, finished_at = j.FinishedAt, error = j.Error }), JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] fetch jobs 失败: {ex.Message}"); RespondText(ctx, 500, "jobs failed"); }
    }

    private void ServeFetchStatus(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var id = ctx.Request.QueryString["id"] ?? ctx.Request.QueryString["job_id"] ?? "";
            if (string.IsNullOrWhiteSpace(id)) { RespondText(ctx, 400, "id required"); return; }
            var job = FetcherService.Get(id);
            if (job == null) { RespondText(ctx, 404, "job not found"); return; }
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { id = job.Id, status = job.Status, total = job.Total, progress = job.Progress, fileName = job.FileName, fileSize = job.FileSize, format = job.Format, preview = job.Preview, created_at = job.CreatedAt, finished_at = job.FinishedAt, error = job.Error }, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] fetch status 失败: {ex.Message}"); RespondText(ctx, 500, "status failed"); }
    }

    private void ServeTrends(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var q = ctx.Request.QueryString;
            var machine = (q["machine"] ?? "").Trim();
            if (machine.Length == 0) machine = null;
            int days = 30;
            if (int.TryParse(q["days"], out var d) && d > 0) days = d;
            var data = ReportService.GetTrend(_db, machine, days);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(data, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] trends 失败: {ex.Message}"); RespondText(ctx, 500, "trends failed"); }
    }

    private void ServeDistribution(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var q = ctx.Request.QueryString;
            var field = (q["field"] ?? "fail_reason").Trim();
            var machine = (q["machine"] ?? "").Trim();
            if (machine.Length == 0) machine = null;
            int limit = 20;
            if (int.TryParse(q["limit"], out var l) && l > 0) limit = l;
            var data = ReportService.GetDistribution(_db, field, machine, limit);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(data, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] distribution 失败: {ex.Message}"); RespondText(ctx, 500, "distribution failed"); }
    }

    #endregion

    #region Lite-Fetch: 数据拉取完整服务端化 + 报告中心 + 程序日志（2026-08-28）— 单一 region，A/B/C 禁改

    private void ServeFetchDownload(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        var id = ctx.Request.QueryString["id"] ?? ctx.Request.QueryString["job_id"] ?? "";
        if (string.IsNullOrWhiteSpace(id)) { RespondText(ctx, 400, "id required"); return; }
        var job = FetcherService.Get(id.Trim());
        if (job == null) { RespondText(ctx, 404, "job not found"); return; }
        if (job.Status != "done" || string.IsNullOrEmpty(job.FilePath) || !File.Exists(job.FilePath))
        {
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = false, status = job.Status, progress = job.Progress, error = job.Error ?? "" }, JsonOpts));
            return;
        }
        try
        {
            var data = File.ReadAllBytes(job.FilePath);
            var ext = Path.GetExtension(job.FilePath).ToLowerInvariant();
            var mime = ext == ".zip" ? "application/zip" : ext == ".csv" ? "text/csv; charset=utf-8" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{Uri.EscapeDataString(job.FileName ?? Path.GetFileName(job.FilePath))}\"";
            try { var (_, who) = ResolveRole(ctx); _db.LogAudit(who ?? "?", "fetch.download", $"job={job.Id} file={job.FileName}"); } catch { }
            Respond(ctx, 200, mime, data);
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] fetch download 失败 {id}: {ex.Message}"); RespondText(ctx, 500, "download failed"); }
    }

    private void ServeFetchProgress(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        var id = ctx.Request.QueryString["id"] ?? "";
        if (string.IsNullOrWhiteSpace(id)) { RespondText(ctx, 400, "id required"); return; }
        var job = FetcherService.Get(id.Trim());
        if (job == null) { RespondText(ctx, 404, "job not found"); return; }
        Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { id = job.Id, status = job.Status, progress = job.Progress, total = job.Total, fileSize = job.FileSize, fileName = job.FileName, error = job.Error, created_at = job.CreatedAt, finished_at = job.FinishedAt }, JsonOpts));
    }

    private void ServeHeatmap(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var q = ctx.Request.QueryString;
            var machine = (q["machine"] ?? "").Trim();
            if (machine.Length == 0) machine = null;
            int days = 30;
            if (int.TryParse(q["days"], out var d) && d > 0) days = d;
            var data = ReportService.GetHeatmap(_db, machine, days);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(data, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] heatmap 失败: {ex.Message}"); RespondText(ctx, 500, "heatmap failed"); }
    }

    private void ServeReportSummary(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        var q = ctx.Request.QueryString;
        var path = q["path"] ?? "";
        var idStr = q["id"] ?? "";
        string? xml = null;
        string fileName = "report.xml";
        string? machineHint = q["machine"];
        if (!string.IsNullOrEmpty(idStr) && long.TryParse(idStr, out var fid))
        {
            var row = _db.GetFailById(fid);
            if (row != null) path = row.XmlPath ?? "";
            fileName = Path.GetFileName(string.IsNullOrEmpty(path) ? "report.xml" : path);
            if (_mesh != null) xml = _mesh.Receiver.FetchXmlForFail(fid);
            else if (!string.IsNullOrEmpty(path) && ResolveFile(path, out _) == 200 && File.Exists(path)) xml = File.ReadAllText(path);
        }
        if (string.IsNullOrEmpty(xml) && !string.IsNullOrEmpty(path))
        {
            fileName = Path.GetFileName(path);
            if (ResolveFile(path, out _) == 200 && File.Exists(path))
                try { xml = File.ReadAllText(path); } catch { }
            if (string.IsNullOrEmpty(xml) && _mesh != null)
                xml = FetchRemoteXmlByPath(machineHint ?? "", path);
        }
        if (string.IsNullOrEmpty(xml)) { RespondText(ctx, 404, "report not found or not in whitelist"); return; }
        try
        {
            var data = XmlParser.ParseReportText(xml);
            if (data.Error) { RespondText(ctx, 500, "xml parse failed"); return; }
            int total = data.Tests.Count;
            int fail = data.Tests.Count(t => t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) && !IsReportIgnored(t.Name));
            int ign = data.Tests.Count(t => t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) && IsReportIgnored(t.Name));
            int pass = data.Tests.Count(t => t.Status.Equals("Passed", StringComparison.OrdinalIgnoreCase));
            var summary = new
            {
                ok = true,
                fileName,
                sn = data.Sn,
                batchTimestamp = data.BatchTimestamp,
                panelStatus = data.PanelStatus,
                tester = data.Tester,
                factoryUser = data.FactoryUser,
                total, fail, ignored = ign, pass,
                tests = data.Tests.Select((t, i) => new { idx = i + 1, name = t.Name, value = t.Value, lolim = t.Lolim, hilim = t.Hilim, unit = t.Unit, status = t.Status, ignored = t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) && IsReportIgnored(t.Name) }).ToList(),
                summaryText = $"SN {data.Sn} · {data.PanelStatus} · {pass} 通过 / {fail} 失败(计不良) / {ign} 排除 · 共 {total} 项",
            };
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(summary, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] report summary 失败: {ex.Message}"); RespondText(ctx, 500, "summary failed"); }
    }

    private static bool IsReportIgnored(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Contains("Get Unit Information", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("UUT Status Err", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void ServeReportArchiveList(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var q = ctx.Request.QueryString;
            var machine = (q["machine"] ?? "").Trim();
            if (machine.Length == 0) machine = null;
            int limit = 50;
            if (int.TryParse(q["limit"], out var l) && l > 0) limit = Math.Min(l, 200);
            int offset = 0;
            if (int.TryParse(q["offset"], out var o) && o >= 0) offset = o;
            var list = _db.ListReportArchives(machine, limit, offset);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(list.Select(x => new { id = x.Id, machine = x.Machine, sn = x.Sn, model = x.Model, test_date = x.TestDate, result = x.Result, xml_path = x.XmlPath, archived_path = x.ArchivedPath, archived_at = x.ArchivedAt, archived_by = x.ArchivedBy, note = x.Note }), JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] report archive list 失败: {ex.Message}"); RespondText(ctx, 500, "archive list failed"); }
    }

    private Task ServeReportArchiveCreate(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "engineer")) return Task.CompletedTask;
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException) { ctx.Response.StatusCode = 413; ctx.Response.KeepAlive = false; return Task.CompletedTask; }
        try
        {
            var (role, who) = ResolveRole(ctx);
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
            var root = doc.RootElement;
            string G(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var xmlPath = G("xml_path") != "" ? G("xml_path") : G("xmlPath");
            var machine = G("machine");
            var sn = G("sn");
            var model = G("model");
            var testDate = G("test_date") != "" ? G("test_date") : G("testDate");
            var result = G("result");
            var note = G("note");
            var idStr = G("id");
            if (!string.IsNullOrEmpty(idStr) && long.TryParse(idStr, out var fid))
            {
                var row = _db.GetFailById(fid);
                if (row != null)
                {
                    machine = row.Machine; sn = row.Sn; model = row.Model; testDate = row.TestDate; result = row.Result; xmlPath = row.XmlPath;
                }
            }
            if (string.IsNullOrWhiteSpace(xmlPath)) { RespondText(ctx, 400, "xml_path or id required"); return Task.CompletedTask; }
            string? xml = null;
            if (ResolveFile(xmlPath, out _) == 200 && File.Exists(xmlPath)) xml = File.ReadAllText(xmlPath);
            else if (_mesh != null) xml = FetchRemoteXmlByPath(machine ?? "", xmlPath);
            string summaryJson = "";
            if (!string.IsNullOrEmpty(xml))
            {
                try
                {
                    var data = XmlParser.ParseReportText(xml);
                    summaryJson = JsonSerializer.Serialize(new { data.Sn, data.PanelStatus, data.BatchTimestamp, data.Tester, total = data.Tests.Count, fail = data.Tests.Count(t => t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) && !IsReportIgnored(t.Name)) }, JsonOpts);
                }
                catch { }
            }
            string archivedPath = "";
            try
            {
                if (!string.IsNullOrEmpty(xml))
                {
                    var safeMachine = string.IsNullOrWhiteSpace(machine) ? "UNKNOWN" : string.Concat(machine.Split(Path.GetInvalidFileNameChars()));
                    var safeSn = string.IsNullOrWhiteSpace(sn) ? "NOSN" : string.Concat(sn.Split(Path.GetInvalidFileNameChars()));
                    var dir = Path.Combine(AppConfig.BaseDir, "data", "report_archive", safeMachine);
                    Directory.CreateDirectory(dir);
                    archivedPath = Path.Combine(dir, $"{safeSn}_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}.xml");
                    File.WriteAllText(archivedPath, xml, new UTF8Encoding(false));
                }
            }
            catch { archivedPath = ""; }
            var entry = new AggDatabase.ReportArchiveEntry
            {
                Machine = machine ?? "", Sn = sn ?? "", Model = model ?? "", TestDate = testDate ?? "", Result = result ?? "", XmlPath = xmlPath, ArchivedPath = archivedPath, ArchivedBy = who ?? "", Note = note ?? "", SummaryJson = summaryJson
            };
            var aid = _db.ArchiveReport(entry);
            _db.LogAudit(who ?? "?", "report.archive", $"#{aid} {machine}/{sn} {xmlPath}");
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, id = aid, archived_path = archivedPath }, JsonOpts));
        }
        catch (JsonException) { RespondText(ctx, 400, "bad json"); }
        catch (Exception ex) { Logger.Warning($"[Web 服务] report archive 失败: {ex.Message}"); RespondText(ctx, 500, "archive failed"); }
        return Task.CompletedTask;
    }

    private void ServeReportCompare(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        var q = ctx.Request.QueryString;
        var p1 = q["path1"] ?? q["path"] ?? q["a"] ?? "";
        var p2 = q["path2"] ?? q["b"] ?? "";
        var id1 = q["id1"] ?? q["id"] ?? "";
        var id2 = q["id2"] ?? "";
        string? xml1 = null, xml2 = null;
        string? m1 = q["machine1"] ?? q["machine"] ?? "";
        string? m2 = q["machine2"] ?? m1 ?? "";
        if (!string.IsNullOrEmpty(id1) && long.TryParse(id1, out var fid1)) xml1 = _mesh?.Receiver.FetchXmlForFail(fid1);
        if (!string.IsNullOrEmpty(id2) && long.TryParse(id2, out var fid2)) xml2 = _mesh?.Receiver.FetchXmlForFail(fid2);
        if (string.IsNullOrEmpty(xml1) && !string.IsNullOrEmpty(p1))
        {
            if (ResolveFile(p1, out _) == 200 && File.Exists(p1)) xml1 = File.ReadAllText(p1);
            else if (_mesh != null) xml1 = FetchRemoteXmlByPath(m1 ?? "", p1);
        }
        if (string.IsNullOrEmpty(xml2) && !string.IsNullOrEmpty(p2))
        {
            if (ResolveFile(p2, out _) == 200 && File.Exists(p2)) xml2 = File.ReadAllText(p2);
            else if (_mesh != null) xml2 = FetchRemoteXmlByPath(m2 ?? "", p2);
        }
        if (string.IsNullOrEmpty(xml1) || string.IsNullOrEmpty(xml2)) { RespondText(ctx, 404, "one or both reports not found"); return; }
        try
        {
            var d1 = XmlParser.ParseReportText(xml1);
            var d2 = XmlParser.ParseReportText(xml2);
            if (d1.Error || d2.Error) { RespondText(ctx, 500, "parse failed"); return; }
            var diff = new
            {
                ok = true,
                batch = new { before = d1.BatchTimestamp, after = d2.BatchTimestamp, same = d1.BatchTimestamp == d2.BatchTimestamp },
                panel = new { before = d1.PanelStatus, after = d2.PanelStatus, same = string.Equals(d1.PanelStatus, d2.PanelStatus, StringComparison.OrdinalIgnoreCase) },
                dut = new { before = d1.Sn, after = d2.Sn, same = d1.Sn == d2.Sn },
                tester = new { before = d1.Tester, after = d2.Tester, same = d1.Tester == d2.Tester },
                summary = new
                {
                    before = new { total = d1.Tests.Count, fail = d1.Tests.Count(t => t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) && !IsReportIgnored(t.Name)), pass = d1.Tests.Count(t => t.Status.Equals("Passed", StringComparison.OrdinalIgnoreCase)) },
                    after = new { total = d2.Tests.Count, fail = d2.Tests.Count(t => t.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase) && !IsReportIgnored(t.Name)), pass = d2.Tests.Count(t => t.Status.Equals("Passed", StringComparison.OrdinalIgnoreCase)) }
                },
                tests = CompareTests(d1.Tests, d2.Tests),
            };
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(diff, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] report compare 失败: {ex.Message}"); RespondText(ctx, 500, "compare failed"); }
    }

    private static List<object> CompareTests(List<XmlParser.ReportTest> a, List<XmlParser.ReportTest> b)
    {
        var mapA = a.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var mapB = b.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var allKeys = new HashSet<string>(mapA.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var k in mapB.Keys) allKeys.Add(k);
        var list = new List<object>();
        foreach (var k in allKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            mapA.TryGetValue(k, out var ta);
            mapB.TryGetValue(k, out var tb);
            if (ta == null) list.Add(new { name = k, status = "added", before = (object?)null, after = tb });
            else if (tb == null) list.Add(new { name = k, status = "removed", before = ta, after = (object?)null });
            else
            {
                bool same = ta.Status == tb.Status && ta.Value == tb.Value && ta.Lolim == tb.Lolim && ta.Hilim == tb.Hilim && ta.Unit == tb.Unit;
                list.Add(new { name = k, status = same ? "unchanged" : "changed", before = ta, after = tb, same });
            }
        }
        return list;
    }

    private Task ServeProcLogCreate(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "engineer")) return Task.CompletedTask;
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException) { ctx.Response.StatusCode = 413; ctx.Response.KeepAlive = false; return Task.CompletedTask; }
        try
        {
            var (role, who) = ResolveRole(ctx);
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(body) ? "{}" : body);
            var root = doc.RootElement;
            string G(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var version = G("version");
            if (string.IsNullOrWhiteSpace(version)) { RespondText(ctx, 400, "version required"); return Task.CompletedTask; }
            var changedAt = G("changed_at") != "" ? G("changed_at") : (G("changedAt") != "" ? G("changedAt") : DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            if (changedAt.Length == 8 && DateTime.TryParseExact(changedAt, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt8)) changedAt = dt8.ToString("yyyy-MM-dd HH:mm:ss");
            else if (DateTime.TryParse(changedAt, out var dtp)) changedAt = dtp.ToString("yyyy-MM-dd HH:mm:ss");
            var changedBy = G("changed_by") != "" ? G("changed_by") : (G("changedBy") != "" ? G("changedBy") : who ?? "");
            var content = G("content") != "" ? G("content") : G("desc") != "" ? G("desc") : G("description");
            string scopeMachines = "";
            if (root.TryGetProperty("scope_machines", out var sm) || root.TryGetProperty("scopeMachines", out sm) || root.TryGetProperty("machines", out sm) || root.TryGetProperty("scope", out sm))
                scopeMachines = sm.ValueKind == JsonValueKind.String ? sm.GetString() ?? "" : sm.GetRawText();
            string paramsSnap = "";
            if (root.TryGetProperty("params_snapshot", out var ps) || root.TryGetProperty("paramsSnapshot", out ps) || root.TryGetProperty("params", out ps))
                paramsSnap = ps.ValueKind == JsonValueKind.String ? ps.GetString() ?? "" : ps.GetRawText();
            string related = "";
            if (root.TryGetProperty("related_reports", out var rr) || root.TryGetProperty("relatedReports", out rr) || root.TryGetProperty("reports", out rr))
                related = rr.ValueKind == JsonValueKind.String ? rr.GetString() ?? "" : rr.GetRawText();
            var entry = new AggDatabase.ProcLogEntry
            {
                Version = version.Trim(), ChangedAt = changedAt, ChangedBy = changedBy?.Trim() ?? "", Content = content ?? "", ScopeMachines = scopeMachines ?? "", ParamsSnapshot = paramsSnap ?? "", RelatedReports = related ?? ""
            };
            var id = _db.CreateProcLog(entry);
            _db.LogAudit(who ?? "?", "proc_log.create", $"#{id} {version} {changedAt}");
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, id }, JsonOpts));
        }
        catch (JsonException) { RespondText(ctx, 400, "bad json"); }
        catch (Exception ex) { Logger.Warning($"[Web 服务] proc-log create 失败: {ex.Message}"); RespondText(ctx, 500, "create failed: " + ex.Message); }
        return Task.CompletedTask;
    }

    private void ServeProcLogList(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var q = ctx.Request.QueryString;
            var machine = (q["machine"] ?? "").Trim(); if (machine == "") machine = null;
            var version = (q["version"] ?? "").Trim(); if (version == "") version = null;
            int limit = 50; if (int.TryParse(q["limit"], out var l) && l > 0) limit = Math.Min(l, 200);
            int offset = 0; if (int.TryParse(q["offset"], out var o) && o >= 0) offset = o;
            var from = (q["from"] ?? q["date_from"] ?? "").Trim(); if (from == "") from = null;
            var to = (q["to"] ?? q["date_to"] ?? "").Trim(); if (to == "") to = null;
            var list = _db.ListProcLogs(machine, version, limit, offset, from, to);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(list.Select(e => new { id = e.Id, version = e.Version, changed_at = e.ChangedAt, changed_by = e.ChangedBy, content = e.Content, scope_machines = e.ScopeMachines, params_snapshot = e.ParamsSnapshot, related_reports = e.RelatedReports, created_at = e.CreatedAt }), JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] proc-log list 失败: {ex.Message}"); RespondText(ctx, 500, "list failed"); }
    }

    private void ServeProcLogTimeline(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var q = ctx.Request.QueryString;
            var machine = (q["machine"] ?? "").Trim(); if (machine == "") machine = null;
            int limit = 50; if (int.TryParse(q["limit"], out var l) && l > 0) limit = Math.Min(l, 200);
            int offset = 0; if (int.TryParse(q["offset"], out var o) && o >= 0) offset = o;
            var list = _db.QueryProcTimeline(machine, limit, offset);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(list.Select(e => new { id = e.Id, version = e.Version, changed_at = e.ChangedAt, changed_by = e.ChangedBy, content = e.Content, scope_machines = e.ScopeMachines, params_snapshot = e.ParamsSnapshot, related_reports = e.RelatedReports }), JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] proc-log timeline 失败: {ex.Message}"); RespondText(ctx, 500, "timeline failed"); }
    }

    private void ServeProcLogDiff(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        var q = ctx.Request.QueryString;
        var id1s = q["id1"] ?? q["a"] ?? q["from"] ?? "";
        var id2s = q["id2"] ?? q["b"] ?? q["to"] ?? "";
        if (!long.TryParse(id1s, out var id1) || !long.TryParse(id2s, out var id2)) { RespondText(ctx, 400, "id1 and id2 required"); return; }
        try
        {
            var diff = _db.DiffProcParams(id1, id2);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, id1, id2, added = diff.Added, removed = diff.Removed, changed = diff.Changed.Select(c => new { key = c.Key, before = c.Before, after = c.After }), unchanged = diff.Unchanged }, JsonOpts));
        }
        catch (InvalidOperationException ex) { RespondText(ctx, 404, ex.Message); }
        catch (Exception ex) { Logger.Warning($"[Web 服务] proc-log diff 失败: {ex.Message}"); RespondText(ctx, 500, "diff failed"); }
    }

    private void ServeProcLogDetail(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        var idStr = ctx.Request.QueryString["id"] ?? "";
        if (!long.TryParse(idStr, out var id)) { RespondText(ctx, 400, "id required"); return; }
        var e = _db.GetProcLog(id);
        if (e == null) { RespondText(ctx, 404, "not found"); return; }
        Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { id = e.Id, version = e.Version, changed_at = e.ChangedAt, changed_by = e.ChangedBy, content = e.Content, scope_machines = e.ScopeMachines, params_snapshot = e.ParamsSnapshot, related_reports = e.RelatedReports, created_at = e.CreatedAt }, JsonOpts));
    }

    #endregion

    #region Lite-Settings: 剩余前端设置与体验层实现（2026-08-28）— 单一 region，A/B/C 禁改

    private Task ServeUsersMe(HttpListenerContext ctx)
    {
        if (!IsAuthenticated(ctx))
        {
            RespondText(ctx, 403, "forbidden: need auth");
            return Task.CompletedTask;
        }
        try
        {
            var (role, who) = ResolveRole(ctx);
            if (role == null) { RespondText(ctx, 403, "forbidden"); return Task.CompletedTask; }
            if (string.Equals(who, "agg_token", StringComparison.OrdinalIgnoreCase))
            {
                var payload = new { ok = true, name = "agg_token", role = "admin", token = _token, layout = (string?)null, favorites = (string?)null, isAggToken = true };
                Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
                return Task.CompletedTask;
            }
            if (string.Equals(who, "anonymous", StringComparison.OrdinalIgnoreCase))
            {
                var anon = new { ok = true, name = "anonymous", role = "admin", token = "", layout = (string?)null, favorites = (string?)null, isAnonymous = true };
                Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(anon, JsonOpts));
                return Task.CompletedTask;
            }
            var u = _db.GetUserByName(who ?? "");
            if (u == null) { RespondText(ctx, 404, "user not found"); return Task.CompletedTask; }
            var dto = new { ok = true, name = u.Name, role = u.Role, token = u.Token, layout = u.Layout, favorites = u.Favorites };
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(dto, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] users/me 失败: {ex.Message}"); RespondText(ctx, 500, "users/me failed"); }
        return Task.CompletedTask;
    }

    private Task ServeUsersMeLayoutGet(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return Task.CompletedTask;
        try
        {
            var (role, who) = ResolveRole(ctx);
            if (string.Equals(who, "agg_token", StringComparison.OrdinalIgnoreCase) || string.Equals(who, "anonymous", StringComparison.OrdinalIgnoreCase))
            {
                Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, layout = (string?)null, owner = who }, JsonOpts));
                return Task.CompletedTask;
            }
            var lay = _db.GetUserLayout(who ?? "");
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, layout = lay, owner = who }, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] layout get 失败: {ex.Message}"); RespondText(ctx, 500, "layout get failed"); }
        return Task.CompletedTask;
    }

    private Task ServeUsersMeLayoutPatch(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return Task.CompletedTask;
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException) { ctx.Response.StatusCode = 413; ctx.Response.KeepAlive = false; return Task.CompletedTask; }
        try
        {
            var (role, who) = ResolveRole(ctx);
            if (string.Equals(who, "agg_token", StringComparison.OrdinalIgnoreCase) || string.Equals(who, "anonymous", StringComparison.OrdinalIgnoreCase))
            {
                Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, saved = false, note = "agg_token/anonymous no user row, only localStorage" }, JsonOpts));
                return Task.CompletedTask;
            }
            string? layoutVal = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("layout", out var lv))
                    {
                        if (lv.ValueKind == JsonValueKind.String) layoutVal = lv.GetString();
                        else if (lv.ValueKind == JsonValueKind.Null) layoutVal = null;
                        else layoutVal = lv.GetRawText();
                    }
                    else if (root.ValueKind == JsonValueKind.String) layoutVal = root.GetString();
                    else if (root.ValueKind == JsonValueKind.Object) layoutVal = root.GetRawText();
                    else layoutVal = body;
                }
                catch { layoutVal = body; }
            }
            if (layoutVal != null && layoutVal.Length > 64 * 1024) { RespondText(ctx, 413, "layout too large"); return Task.CompletedTask; }
            var ok = _db.SetUserLayout(who ?? "", layoutVal);
            if (!ok) { RespondText(ctx, 404, "user not found"); return Task.CompletedTask; }
            try { var (_, w2) = ResolveRole(ctx); _db.LogAudit(w2 ?? "?", "user.layout.save", (layoutVal?.Length ?? 0).ToString()); } catch { }
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, saved = true }, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] layout patch 失败: {ex.Message}"); RespondText(ctx, 500, "layout save failed"); }
        return Task.CompletedTask;
    }

    private Task ServeUsersMeFavGet(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return Task.CompletedTask;
        try
        {
            var (role, who) = ResolveRole(ctx);
            if (string.Equals(who, "agg_token", StringComparison.OrdinalIgnoreCase) || string.Equals(who, "anonymous", StringComparison.OrdinalIgnoreCase))
            {
                Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, favorites = (string?)null, owner = who }, JsonOpts));
                return Task.CompletedTask;
            }
            var fav = _db.GetUserFavorites(who ?? "");
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, favorites = fav, owner = who }, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] favorites get 失败: {ex.Message}"); RespondText(ctx, 500, "favorites get failed"); }
        return Task.CompletedTask;
    }

    private Task ServeUsersMeFavPatch(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return Task.CompletedTask;
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException) { ctx.Response.StatusCode = 413; ctx.Response.KeepAlive = false; return Task.CompletedTask; }
        try
        {
            var (role, who) = ResolveRole(ctx);
            if (string.Equals(who, "agg_token", StringComparison.OrdinalIgnoreCase) || string.Equals(who, "anonymous", StringComparison.OrdinalIgnoreCase))
            {
                Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, saved = false, note = "agg_token/anonymous no user row" }, JsonOpts));
                return Task.CompletedTask;
            }
            string? favVal = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("favorites", out var fv))
                    {
                        if (fv.ValueKind == JsonValueKind.String) favVal = fv.GetString();
                        else if (fv.ValueKind == JsonValueKind.Null) favVal = null;
                        else favVal = fv.GetRawText();
                    }
                    else if (root.ValueKind == JsonValueKind.String) favVal = root.GetString();
                    else if (root.ValueKind == JsonValueKind.Object) favVal = root.GetRawText();
                    else favVal = body;
                }
                catch { favVal = body; }
            }
            if (favVal != null && favVal.Length > 64 * 1024) { RespondText(ctx, 413, "favorites too large"); return Task.CompletedTask; }
            var ok = _db.SetUserFavorites(who ?? "", favVal);
            if (!ok) { RespondText(ctx, 404, "user not found"); return Task.CompletedTask; }
            try { var (_, w2) = ResolveRole(ctx); _db.LogAudit(w2 ?? "?", "user.favorites.save", (favVal?.Length ?? 0).ToString()); } catch { }
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, saved = true }, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] favorites patch 失败: {ex.Message}"); RespondText(ctx, 500, "favorites save failed"); }
        return Task.CompletedTask;
    }

    private void ServeSearch(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var q = (ctx.Request.QueryString["q"] ?? ctx.Request.QueryString["query"] ?? "").Trim();
            int limit = 8;
            if (int.TryParse(ctx.Request.QueryString["limit"], out var l) && l > 0) limit = Math.Min(l, 20);
            if (string.IsNullOrWhiteSpace(q))
            {
                Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, q = "", tokens = Array.Empty<string>(), counts = new { fails = 0, maintenance = 0, devices = 0, yields = 0 }, total = 0, results = Array.Empty<object>() }, JsonOpts));
                return;
            }
            if (q.Length > 200) q = q.Substring(0, 200);
            var tokens = q.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                          .Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToArray();
            var fails = new List<object>();
            try
            {
                var allFails = _db.QueryFails(500, null, 0, null);
                var filtered = allFails.Where(r =>
                {
                    var hay = $"{r.Machine} {r.FailReason} {r.Sn} {r.Model} {r.Tester} {r.StationId}".ToLowerInvariant();
                    return tokens.All(tok => hay.Contains(tok));
                }).Take(limit).ToList();
                foreach (var r in filtered)
                    fails.Add(new { type = "fail", id = r.Id, title = r.FailReason ?? r.Sn ?? $"FAIL #{r.Id}", subtitle = $"{r.Machine} · {r.Sn} · {r.Model}", ts = r.IngestTs ?? r.Ts, link = $"#/fails?q={Uri.EscapeDataString(q)}&id={r.Id}" });
            }
            catch { }
            var maints = new List<object>();
            try
            {
                var allM = _db.ListMaintenance("", 500);
                var filteredM = allM.Where(r =>
                {
                    var hay = $"{r.FailItem} {r.FailReason} {r.Resolver} {r.Status} {r.StationId} {r.Notes}".ToLowerInvariant();
                    return tokens.All(tok => hay.Contains(tok));
                }).Take(limit).ToList();
                foreach (var r in filteredM)
                    maints.Add(new { type = "maintenance", id = r.Id, title = r.FailItem, subtitle = $"{r.Status} · {r.Resolver} · {r.StationId}", ts = r.UpdatedAt, link = $"#/maintenance?q={Uri.EscapeDataString(q)}&id={r.Id}" });
            }
            catch { }
            var devs = new List<object>();
            try
            {
                var allD = _db.ListDeviceInfos();
                var filteredD = allD.Where(d =>
                {
                    var hay = $"{d.Machine} {d.Hostname} {d.Ip} {d.CpuModel} {d.Os}".ToLowerInvariant();
                    return tokens.All(tok => hay.Contains(tok));
                }).Take(limit).ToList();
                foreach (var d in filteredD)
                    devs.Add(new { type = "device", id = d.Machine, title = d.Machine, subtitle = $"{d.Hostname} {d.Ip} {(d.Online ? "在线" : "离线")}", ts = d.LastSeen, link = $"#/devices?q={Uri.EscapeDataString(q)}&machine={Uri.EscapeDataString(d.Machine)}" });
            }
            catch { }
            var yields = new List<object>();
            try
            {
                var allY = _db.QueryDailyStats(null, null, null, 200);
                var filteredY = allY.Where(y =>
                {
                    var hay = $"{y.Machine} {y.TestDate}".ToLowerInvariant();
                    return tokens.All(tok => hay.Contains(tok));
                }).Take(limit).ToList();
                foreach (var y in filteredY)
                {
                    var rate = y.Total > 0 ? Math.Round(y.Pass * 100.0 / y.Total, 2) : 100.0;
                    yields.Add(new { type = "yield", id = $"{y.Machine}_{y.TestDate}", title = $"{y.Machine} {y.TestDate}", subtitle = $"良率 {rate}% ({y.Pass}/{y.Total})", ts = y.UpdatedTs, link = $"#/yield?q={Uri.EscapeDataString(q)}&machine={Uri.EscapeDataString(y.Machine)}&date={y.TestDate}" });
                }
            }
            catch { }
            var allResults = new List<object>();
            allResults.AddRange(fails); allResults.AddRange(maints); allResults.AddRange(devs); allResults.AddRange(yields);
            var payload = new { ok = true, q, tokens, counts = new { fails = fails.Count, maintenance = maints.Count, devices = devs.Count, yields = yields.Count }, total = allResults.Count, results = new { fails, maintenance = maints, devices = devs, yields }, flat = allResults.Take(20).ToList() };
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] search 失败: {ex.Message}"); RespondText(ctx, 500, "search failed"); }
    }

    #endregion

    #region Lite-Infra: 告警规则中心 + Gossiper自适应 + 多机台对比实现（2026-08-28）— 单一 region，A/B/C 禁改

    private void ServeAlertRules(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var rules = AggAlertService.GetAlertRulesSnapshot();
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(rules, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] alert rules 失败: {ex.Message}"); RespondText(ctx, 500, "rules failed"); }
    }

    private void ServeAlertHistory(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var q = ctx.Request.QueryString;
            var machine = (q["machine"] ?? "").Trim(); if (machine == "") machine = null;
            var rule = (q["rule"] ?? "").Trim(); if (rule == "") rule = null;
            int limit = 50; if (int.TryParse(q["limit"], out var l) && l > 0) limit = Math.Min(l, 200);
            int offset = 0; if (int.TryParse(q["offset"], out var o) && o >= 0) offset = o;
            var list = _db.ListAlertHistory(machine, rule, limit, offset);
            var total = _db.CountAlertHistory(machine, rule);
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, total, count = list.Count, rows = list }, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] alert history 失败: {ex.Message}"); RespondText(ctx, 500, "history failed"); }
    }

    private void ServeGossiperStatus(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var g = _mesh?.Gossiper;
            if (g == null) { RespondText(ctx, 404, "gossiper not available (single node)"); return; }
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new
            {
                ok = true,
                interval_sec = g.CurrentIntervalSec,
                reason = g.AdaptiveReason,
                gossip_count = g.GossipCount,
                last_gap = g.LastGapCount,
                last_at = g.LastGossipAt,
            }, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] gossiper status 失败: {ex.Message}"); RespondText(ctx, 500, "gossiper failed"); }
    }

    private void ServeCompareTrends(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var q = ctx.Request.QueryString;
            var machinesParam = (q["machines"] ?? q["machine"] ?? "").Trim();
            int days = 7; if (int.TryParse(q["days"], out var d) && d > 0) days = Math.Clamp(d, 1, 30);
            if (string.IsNullOrEmpty(machinesParam))
            {
                var m = (q["machine"] ?? "").Trim(); if (m == "") m = null;
                var data = ReportService.GetTrend(_db, m, days);
                Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, days, machines = m == null ? Array.Empty<string>() : new[] { m }, trends = new Dictionary<string, object> { ["all"] = data } }, JsonOpts));
                return;
            }
            var machines = machinesParam.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray();
            var result = new Dictionary<string, object>();
            foreach (var m in machines)
            {
                try { result[m] = ReportService.GetTrend(_db, m, days); } catch { result[m] = Array.Empty<object>(); }
            }
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, days, machines, trends = result }, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] compare trends 失败: {ex.Message}"); RespondText(ctx, 500, "compare trends failed"); }
    }

    private void ServeCompareDistribution(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var q = ctx.Request.QueryString;
            var machinesParam = (q["machines"] ?? q["machine"] ?? "").Trim();
            var field = (q["field"] ?? "fail_reason").Trim();
            int limit = 10; if (int.TryParse(q["limit"], out var l) && l > 0) limit = Math.Clamp(l, 1, 20);
            if (string.IsNullOrEmpty(machinesParam))
            {
                var m = (q["machine"] ?? "").Trim(); if (m == "") m = null;
                var data = ReportService.GetDistribution(_db, field, m, limit);
                Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, field, limit, machines = m == null ? Array.Empty<string>() : new[] { m }, distributions = new Dictionary<string, object> { ["all"] = data } }, JsonOpts));
                return;
            }
            var machines = machinesParam.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray();
            var result = new Dictionary<string, object>();
            foreach (var m in machines)
            {
                try { result[m] = ReportService.GetDistribution(_db, field, m, limit); } catch { result[m] = Array.Empty<object>(); }
            }
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = true, field, limit, machines, distributions = result }, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] compare distribution 失败: {ex.Message}"); RespondText(ctx, 500, "compare distribution failed"); }
    }

    #endregion

    #region Lite-Ops: 告警规则热更新实现（2026-08-28）— 单一 region，A/B/C/Lite-Fetch/Lite-Settings/Lite-Infra 禁改

    private void ServeAlertRulesPatch(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "admin")) return;
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException)
        {
            ctx.Response.StatusCode = 413;
            ctx.Response.KeepAlive = false;
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var cfg = AppConfig.Instance;
            var changed = new List<string>();

            if (root.TryGetProperty("disk", out var dEl) && dEl.ValueKind == JsonValueKind.Object)
            {
                bool on = ReadRuleEnabled(dEl, cfg.DeviceAlertDiskFreeGb > 0);
                double v = on ? ReadRuleThreshold(dEl, cfg.DeviceAlertDiskFreeGb, 10) : 0;
                if (v < 0 || v > 100000) { RespondText(ctx, 400, "disk 阈值需在 0~100000 GB（0=关闭）"); return; }
                if (Math.Abs(v - cfg.DeviceAlertDiskFreeGb) > 1e-9) { cfg.DeviceAlertDiskFreeGb = v; changed.Add("device_alert_disk_free_gb"); }
            }
            if (root.TryGetProperty("cpu", out var cEl) && cEl.ValueKind == JsonValueKind.Object)
            {
                bool on = ReadRuleEnabled(cEl, cfg.DeviceAlertCpuPct > 0);
                double v = on ? ReadRuleThreshold(cEl, cfg.DeviceAlertCpuPct, 90) : 0;
                if (v < 0 || v > 100) { RespondText(ctx, 400, "cpu 阈值需在 0~100 %（0=关闭）"); return; }
                var iv = (int)Math.Round(v);
                if (iv != cfg.DeviceAlertCpuPct) { cfg.DeviceAlertCpuPct = iv; changed.Add("device_alert_cpu_pct"); }
            }
            if (root.TryGetProperty("offline", out var oEl) && oEl.ValueKind == JsonValueKind.Object)
            {
                bool on = ReadRuleEnabled(oEl, cfg.DeviceAlertOfflineMinutes > 0);
                double v = on ? ReadRuleThreshold(oEl, cfg.DeviceAlertOfflineMinutes, 5) : 0;
                if (v < 0 || v > 10080) { RespondText(ctx, 400, "offline 阈值需在 0~10080 分钟（0=关闭）"); return; }
                var iv = (int)Math.Round(v);
                if (iv != cfg.DeviceAlertOfflineMinutes) { cfg.DeviceAlertOfflineMinutes = iv; changed.Add("device_alert_offline_minutes"); }
            }
            if (root.TryGetProperty("yield", out var yEl) && yEl.ValueKind == JsonValueKind.Object)
            {
                bool on = ReadRuleEnabled(yEl, cfg.YieldAlertEnabled && cfg.YieldAlertYieldPct > 0);
                if (on != cfg.YieldAlertEnabled) { cfg.YieldAlertEnabled = on; changed.Add("yield_alert_enabled"); }
                if (on)
                {
                    double v = ReadRuleThreshold(yEl, cfg.YieldAlertYieldPct, 90);
                    if (v < 0 || v > 100) { RespondText(ctx, 400, "yield 阈值需在 0~100 %"); return; }
                    if (Math.Abs(v - cfg.YieldAlertYieldPct) > 1e-9) { cfg.YieldAlertYieldPct = v; changed.Add("yield_alert_yield_pct"); }
                }
            }

            if (changed.Count == 0)
            {
                Respond(ctx, 200, "application/json; charset=utf-8",
                    Encoding.UTF8.GetBytes("{\"ok\":true,\"changed\":[],\"msg\":\"无变化\",\"rules\":" + JsonSerializer.Serialize(AggAlertService.GetAlertRulesSnapshot(), JsonOpts) + "}"));
                return;
            }

            if (!cfg.Save())
            {
                RespondText(ctx, 500, "告警规则保存失败（看日志）");
                return;
            }

            try { SettingsChanged?.Invoke(); } catch (Exception ex) { Logger.Warning($"[Web 服务] 告警规则变更回调异常: {ex.Message}"); }

            try { var (_, who) = ResolveRole(ctx); _db.LogAudit(who ?? "?", "alerts.rules.save", string.Join(",", changed)); }
            catch (Exception ex) { Logger.Warning($"[Web 服务] 审计写入失败: {ex.Message}"); }

            var respJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["changed"] = changed,
                ["msg"] = "已保存并立即生效（无需重启）",
                ["rules"] = AggAlertService.GetAlertRulesSnapshot(),
            }, JsonOpts);
            Respond(ctx, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(respJson));
        }
        catch (JsonException ex)
        {
            Logger.Warning($"[Web 服务] 告警规则 JSON 非法: {ex.Message}");
            ctx.Response.StatusCode = 400;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 告警规则更新失败: {ex.Message}");
            RespondText(ctx, 500, "alerts rules update failed");
        }
    }

    private static bool ReadRuleEnabled(JsonElement el, bool fallback)
    {
        if (el.TryGetProperty("enabled", out var e))
        {
            if (e.ValueKind == JsonValueKind.True) return true;
            if (e.ValueKind == JsonValueKind.False) return false;
        }
        return fallback;
    }

    private static double ReadRuleThreshold(JsonElement el, double current, double factoryDefault)
    {
        foreach (var name in new[] { "threshold_gb", "threshold_pct", "threshold_minutes", "threshold" })
        {
            if (el.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetDouble(out var v))
                return v > 0 ? v : factoryDefault;
        }
        return current > 0 ? current : factoryDefault;
    }

    #endregion

    private static string SerializeFailRow(AggFailRow row)
    {
        var data = new Dictionary<string, object?>
        {
            ["station_id"] = row.StationId, ["model"] = row.Model, ["category"] = row.Category,
            ["test_date"] = row.TestDate, ["sn"] = row.Sn, ["result"] = row.Result,
            ["xml_path"] = row.XmlPath, ["fail_reason"] = row.FailReason, ["tester"] = row.Tester,
            ["panel_status"] = row.PanelStatus, ["batch_timestamp"] = row.BatchTimestamp,
            ["has_fail_items"] = row.HasFailItems ? 1 : 0, ["file_size"] = row.FileSize,
            ["xml_available"] = 1,
        };
        if (!string.IsNullOrEmpty(row.FixtureId)) data["fixture_id"] = row.FixtureId;
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["machine"] = row.Machine, ["type"] = "fail", ["seq"] = row.Seq,
            ["ts"] = row.Ts, ["data"] = data,
        });
    }

    private int ResolveFile(string path, out string? full)
    {
        full = null;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 路径非法 '{path}': {ex.Message}");
            return 400;
        }

        if (string.Equals(full, LogFileFullPath, StringComparison.OrdinalIgnoreCase))
            return File.Exists(full) ? 200 : 403;

        if (full.StartsWith(AggXmlRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return File.Exists(full) ? 200 : 403;

        if (string.IsNullOrEmpty(_resultsRoot)) return 403;

        string root;
        try { root = Path.GetFullPath(_resultsRoot); }
        catch { return 400; }
        var rootSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootSep, StringComparison.OrdinalIgnoreCase)) return 403;
        if (HasReparseComponent(full, root))
        {
            Logger.Warning($"[Web 服务] 拒绝含链接(reparse point)的路径: {full}");
            return 403;
        }
        if (!File.Exists(full)) return 403;
        return 200;
    }

    private static bool HasReparseComponent(string full, string root)
    {
        try
        {
            var rel = full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (rel.Length == 0) return false;
            var cur = root.TrimEnd(Path.DirectorySeparatorChar);
            foreach (var part in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                cur = Path.Combine(cur, part);
                var attr = File.GetAttributes(cur);
                if ((attr & FileAttributes.ReparsePoint) != 0) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private int ResolveDir(string path, out string? full)
    {
        full = null;
        string target;
        try { target = !string.IsNullOrEmpty(path) && !Path.IsPathRooted(path) ? Path.Combine(_resultsRoot, path) : path; }
        catch { return 400; }
        try { full = Path.GetFullPath(target); }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 路径非法 '{path}': {ex.Message}");
            return 400;
        }

        if (string.IsNullOrEmpty(_resultsRoot)) return 403;

        string root;
        try { root = Path.GetFullPath(_resultsRoot); }
        catch { return 400; }
        if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            var rootSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootSep, StringComparison.OrdinalIgnoreCase)) return 403;
            if (HasReparseComponent(full, root)) return 403;
        }
        if (!Directory.Exists(full)) return 404;
        return 200;
    }

    private static string ContentTypeFor(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".xml" => "application/xml",
            ".txt" or ".log" or ".csv" or ".ini" or ".json" => "text/plain; charset=utf-8",
            _ => "application/octet-stream",
        };
    }

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

    private static void Respond(HttpListenerContext ctx, int status, string contentType, byte[] body)
    {
        var resp = ctx.Response;
        resp.StatusCode = status;
        resp.ContentType = contentType;
        resp.Headers["X-Content-Type-Options"] = "nosniff";
        resp.ContentLength64 = body.Length;
        if (body.Length > 0) resp.OutputStream.Write(body, 0, body.Length);
    }

    private static void RespondText(HttpListenerContext ctx, int status, string text)
        => Respond(ctx, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

    private static string ReadBody(HttpListenerRequest req)
    {
        if (req.ContentLength64 > MaxBodyBytes)
        {
            DrainBody(req);
            throw new BodyTooLargeException();
        }
        using var ms = new MemoryStream();
        var buf = new byte[ReadChunk];
        while (true)
        {
            int n = req.InputStream.Read(buf, 0, buf.Length);
            if (n <= 0) break;
            if (ms.Length + n > MaxBodyBytes)
            {
                DrainBody(req);
                throw new BodyTooLargeException();
            }
            ms.Write(buf, 0, n);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void DrainBody(HttpListenerRequest req)
    {
        try
        {
            var buf = new byte[ReadChunk];
            long drained = 0;
            while (drained < MaxDrainBytes && req.InputStream.Read(buf, 0, buf.Length) > 0) drained += buf.Length;
        }
        catch {  }
    }

    private const long MaxDrainBytes = 8 * 1024 * 1024;

    private sealed class BodyTooLargeException : Exception { }

    private const string DashboardHtml = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Argus 多机台聚合看板</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body { background: #f7f7f7; color: #1a1a1a; font-family: "Microsoft YaHei", "PingFang SC", "Segoe UI", sans-serif; padding: 16px; }
  header { display: flex; align-items: center; gap: 14px; flex-wrap: wrap; margin-bottom: 16px; }
  h1 { font-size: 20px; color: #141414; }
  .stat { color: #c8102e; font-weight: bold; }
  button { background: #ffffff; color: #1a1a1a; border: 1px solid #c9c9c9; border-radius: 4px; padding: 6px 14px; cursor: pointer; font-size: 13px; }
  button:hover { background: #efefef; }
  label { color: #8c8c8c; font-size: 13px; cursor: pointer; }
  .tabs { display: flex; gap: 8px; margin-bottom: 16px; }
  .tab.active { background: #f7dfe3; border-color: #c8102e; }
  .files-bar { color: #8c8c8c; font-size: 13px; margin-bottom: 10px; }
  #fileRoot { color: #c8102e; font-family: Consolas, "Courier New", monospace; }
  .crumb { margin-bottom: 10px; font-size: 13px; }
  .crumb a { color: #c8102e; text-decoration: none; margin-right: 6px; }
  .crumb a:hover { text-decoration: underline; }
  .crumb .sep { color: #b3b3b3; margin-right: 6px; }
  td.fname.dir { color: #c8102e; cursor: pointer; font-weight: bold; }
  td.fname.file { cursor: pointer; }
  #fileMsg { color: #c8102e; font-size: 13px; margin-top: 10px; }
  .cards { display: flex; flex-wrap: wrap; gap: 12px; margin-bottom: 20px; }
  .card { background: #ffffff; border: 1px solid #e3e3e3; border-left: 4px solid #141414; border-radius: 6px; padding: 10px 14px; min-width: 178px; font-size: 13px; line-height: 1.7; }
  .card.offline { border-left-color: #c8102e; opacity: .85; }
  .card .mname { font-size: 15px; font-weight: bold; color: #141414; }
  .dot { font-size: 12px; }
  .dot.on { color: #141414; }
  .dot.off { color: #c8102e; }
  .fail { color: #c8102e; font-weight: bold; }
  table { width: 100%; border-collapse: collapse; background: #ffffff; font-size: 13px; }
  th, td { border: 1px solid #e3e3e3; padding: 6px 8px; text-align: left; white-space: nowrap; }
  th { background: #f7f7f7; color: #595959; font-weight: normal; }
  tr:hover td { background: #fafafa; }
  td.reason { max-width: 320px; overflow: hidden; text-overflow: ellipsis; }
  .empty { color: #b3b3b3; text-align: center; padding: 24px !important; white-space: normal !important; }
  a.xml { color: #c8102e; text-decoration: none; }
  a.xml:hover { text-decoration: underline; }
  #exportLink { display: inline-block; margin-top: 14px; color: #c8102e; text-decoration: none; font-size: 13px; }
  #exportLink:hover { text-decoration: underline; }
  .filters { display: flex; gap: 8px; margin-bottom: 10px; align-items: center; }
  .filters input { background: #ffffff; border: 1px solid #c9c9c9; color: #1a1a1a; border-radius: 4px; padding: 6px 10px; font-size: 13px; flex: 1; max-width: 360px; }
  .filters input:focus { outline: none; border-color: #c8102e; }
  .pager { display: flex; gap: 10px; margin-top: 12px; align-items: center; color: #8c8c8c; font-size: 13px; }
  .pager button:disabled { opacity: .4; cursor: default; }
  #settingsView h3 { color: #141414; font-size: 15px; margin-bottom: 12px; }
  #settingsView .set-row { display: flex; align-items: center; gap: 10px; margin-bottom: 10px; }
  #settingsView .set-row label { width: 170px; color: #595959; font-size: 13px; flex-shrink: 0; }
  #settingsView .set-row input, #settingsView .set-row select {
    background: #ffffff; border: 1px solid #c9c9c9; color: #1a1a1a; border-radius: 4px;
    padding: 6px 10px; font-size: 13px; width: 360px;
  }
  #settingsView .set-row input:focus, #settingsView .set-row select:focus { outline: none; border-color: #c8102e; }
  #settingsView .set-row .hint { color: #8c8c8c; font-size: 12px; }
  #settingsView .set-actions { margin-top: 16px; display: flex; align-items: center; gap: 12px; }
  #setMsg { color: #c8102e; font-size: 13px; }
  #setMsg.err { color: #c8102e; }
  footer { margin-top: 18px; color: #8c8c8c; font-size: 12px; line-height: 1.8; }
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
    <input type="number" id="setPort" min="1" max="65535" value="8081">
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

function hasTokenCookie() {
  return document.cookie.indexOf("agg_token=") >= 0;
}

function xmlHref(p) {
  // 服务端认证后会下发 HttpOnly Cookie：链接自动携带，不再把 token 拼进 URL
  //（防进浏览器历史/代理日志）；无 Cookie 的旧客户端回退 ?token= 兼容。
  if (hasTokenCookie()) return p;
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
  if (el && AGG_TOKEN && !hasTokenCookie()) el.href = "/api/export.csv?token=" + encodeURIComponent(AGG_TOKEN);
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
  document.getElementById("setPort").value = s.mesh_port || 8081;
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
    mesh_port: isNaN(port) ? 8081 : port,
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
