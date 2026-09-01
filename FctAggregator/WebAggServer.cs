using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace FctAggregator;

/// <summary>
/// 聚合端纯 Web 服务（Phase 3 新增：聚合端 Web 模式）。
///
/// 机台上跑 `Argus.exe agg --web`（无窗体常驻），本服务同时承载：
///   1. 机台推送入口（v3.8.0 对齐：唯一入口为 POST /api/mesh/fail、/api/mesh/heartbeat；
///      POST / 按 body type 分发，兼容遗留 AgentPusher）；
///   2. 浏览器看板（GET）：机台状态卡片、FAIL 明细表、XML 报告下载、CSV 导出、健康检查。
///
/// 路由表：
///   GET  /                    看板页（v3.8.0：优先服务 public/ 新 SPA，缺失回退内嵌旧看板）
///   GET  /legacy              旧内嵌单页看板（v3.8.0 起从 / 移出，保留给老客户端/新 SPA 管理页链接）
///   GET  /public/*            模块化 SPA 静态资源（v3.8.0）
///   GET  /api/machines        机台状态列表 JSON（AggMachineStatus 数组）
///   GET  /api/fails?limit&machine  FAIL 明细 JSON（AggFailRow 数组；limit 默认 200，上限 1000）
///   GET  /api/fails/count      FAIL 总数（分页用）
///   GET  /api/file?id=|path=  XML 报告下载（白名单校验；id= 场景本地缺失时回退跨机拉取）
///   GET  /api/list?path=      目录浏览：resultsRoot 白名单内子目录列表（见 <see cref="ResolveDir"/>）
///   GET  /api/export.csv?limit&machine  FAIL 明细 CSV（UTF-8 BOM + CWE-1236 公式注入防护）
///   GET  /api/xmlview?id=     在线 XML 报告（服务端渲染 HTML，等效本地查看器）
///   GET  /api/health          {"ok":true}（部署排查用）
///   GET  /api/stats           良率日统计（v3.8.0 P1 D1，yld_daily）
///   GET  /api/todos           全局待办池（跨机台，v3.5.0）
///   GET/POST /api/settings    设置读取/保存（管理后台）
///   POST /api/login | GET /api/status /api/audit /api/users  （v3.8.0 P1 D3 权限/审计）
///   POST /api/mesh/fail|heartbeat|event  节点入站（fail/heartbeat=推送；event=全局待办）
///   GET  /api/mesh/summary|fetch|peers|xml   节点间对账 / 邻居视图 / 跨机 XML 拉取
///   POST /                    按 body 顶层 type 分发（fail/heartbeat；遗留 AgentPusher 兼容）
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
    /// <summary>遗留中心监听（v3.5.0 前）；去中心化下为 null，改用 <see cref="_mesh"/>。</summary>
#pragma warning disable CS0618
    private readonly AggWatcher? _watcher;
#pragma warning restore CS0618
    /// <summary>P2P 节点（v3.5.0 去中心化）：入站接收/对账/Gossiper 均经此。非 null 时优先于 _watcher。</summary>
    private readonly MeshNode? _mesh;
    private readonly AggDatabase _db;
    private readonly string _resultsRoot;
    private readonly string _shareRoot;
    private string _token;   // 共享 token（agg_token）；空串 = 不启用鉴权。设置页可热更新

    private readonly object _lock = new();
    private HttpListener? _listener;
    private Thread? _acceptThread;
    private long _receivedCount;
    private DateTime _startedAt;   // 服务启动时间（看板页脚健康展示，v3.4.11）

    /// <summary>极简路由管线：路由注册与分发（精确/通配、404/405、命中计数），替代原 Handle() 大 switch。</summary>
    private readonly RoutePipeline _pipeline = new();

    /// <summary>构造（遗留中心模式，v3.5.0 前）：watcher=聚合监听器，db=聚合库，resultsRoot=XML 白名单根，shareRoot=共享目录。</summary>
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
        RegisterRoutes();   // 路由注册放基础构造：遗留中心与去中心化（MeshNode 构造链式走此）共用同一路由表
    }
#pragma warning restore CS0618

    /// <summary>构造（v3.5.0 去中心化）：mesh=P2P 节点（入站接收/peer 视图/对账），db=本地副本库。
    /// 看板与 mesh 入站共同一 HttpListener 端口，单端口服务。</summary>
    public WebAggServer(int port, MeshNode mesh, AggDatabase db, string resultsRoot, string shareRoot, string token = "")
#pragma warning disable CS0618   // 链式复用旧构造（遗留中心模式）
        : this(port, (AggWatcher?)null, db, resultsRoot, shareRoot, token)
#pragma warning restore CS0618
    {
        _mesh = mesh;
        // 安全加固（H-1）：/api/mesh/xml 的本地读取必须过文件白名单，
        // 否则推送方可伪造 data.xml_path 借本服务读任意文件（跨机 HTTP 拉取不受影响）
        mesh.Receiver.LocalReadValidator = path => ResolveFile(path, out _) == 200;
    }

    /// <summary>
    /// 注册全部路由：与原 Handle() 大 switch 全量一一对应（含 switch 之前的 /public/* 静态分支），一个端点不能漏。
    /// 每次命中先 CountHit()（保持「命中即计数」原行为），再调对应 ServeXxx；handler 均为同步 void 方法，
    /// 包装返回 Task.CompletedTask。注册点放基础构造（遗留中心）：MeshNode 构造链式走此构造，
    /// 生产（去中心化）与 selftest（遗留构造）共用同一份路由表，行为一致。
    /// </summary>
    private void RegisterRoutes()
    {
        _pipeline.Add("GET", "/", ctx => { CountHit(); ServeDashboard(ctx); return Task.CompletedTask; });
        // v3.8.0 对齐：旧内嵌单页看板移到 /legacy（根路径优先服务 public/ SPA）
        _pipeline.Add("GET", "/legacy", ctx => { CountHit(); ServeLegacyDashboard(ctx); return Task.CompletedTask; });
        _pipeline.Add("POST", "/", ctx => { CountHit(); ServePush(ctx, null); return Task.CompletedTask; });

        _pipeline.Add("GET", "/api/health", ctx => { CountHit(); ServeHealth(ctx); return Task.CompletedTask; });

        _pipeline.Add("GET", "/api/settings", ctx => { CountHit(); ServeSettings(ctx); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/settings", ctx => { CountHit(); ServeSettingsSave(ctx); return Task.CompletedTask; });
        // 域3 配置智能化（v3.19.0）：校验/推荐/回滚
        _pipeline.Add("GET", "/api/config/validate", ctx => { CountHit(); ServeConfigValidate(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/config/recommend", ctx => { CountHit(); ServeConfigRecommend(ctx); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/config/rollback", ctx => { CountHit(); ServeConfigRollback(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/config/backups", ctx => { CountHit(); ServeConfigBackups(ctx); return Task.CompletedTask; });
        // 域4 设备智能化（v3.19.0）：预测/巡检/变更
        _pipeline.Add("GET", "/api/devices/predict", ctx => { CountHit(); ServeDevicesPredict(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/devices/inspect", ctx => { CountHit(); ServeDevicesInspect(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/fct/changes", ctx => { CountHit(); ServeFctChanges(ctx); return Task.CompletedTask; });
        // 域7 维修/待办智能（v3.19.0）：优先级/流转
        _pipeline.Add("GET", "/api/todos/suggest", ctx => { CountHit(); ServeTodoSuggest(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/maintenance/advise", ctx => { CountHit(); ServeMaintenanceAdvise(ctx); return Task.CompletedTask; });

        _pipeline.Add("GET", "/api/machines", ctx => { CountHit(); ServeMachines(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/fails", ctx => { CountHit(); ServeFails(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/fails/count", ctx => { CountHit(); ServeFailsCount(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/file", ctx => { CountHit(); ServeFile(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/list", ctx => { CountHit(); ServeList(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/export.csv", ctx => { CountHit(); ServeCsv(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/xmlview", ctx => { CountHit(); ServeXmlView(ctx); return Task.CompletedTask; });
        // v3.8.0 P1 D1：良率日统计（yld_daily，心跳携带），总纲 L1 的 /api/stats
        _pipeline.Add("GET", "/api/stats", ctx => { CountHit(); ServeStats(ctx); return Task.CompletedTask; });

        // ===== v3.8.0 P1 D3：权限（users 三角色）+ 审计 + 状态 =====
        // /api/login 在 Handle 层豁免 agg_token 校验（未登录也能到达），其余路由照常
        // （async handler 直接返回其 Task，避免 CS4014 未等待警告）
        _pipeline.Add("POST", "/api/login", ctx => { CountHit(); return ServeLogin(ctx); });
        _pipeline.Add("GET", "/api/status", ctx => { CountHit(); ServeStatus(ctx); return Task.CompletedTask; });
        // v3.10.0 P1-1（架构方案 P1-A）：指标（吞吐/积压/失败/队列/503），脚本与看板拉取
        _pipeline.Add("GET", "/api/metrics", ctx => { CountHit(); ServeMetrics(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/audit", ctx => { CountHit(); ServeAudit(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/users", ctx => { CountHit(); return ServeUsers(ctx); });
        _pipeline.Add("POST", "/api/users", ctx => { CountHit(); return ServeUsers(ctx); });
        _pipeline.Add("DELETE", "/api/users", ctx => { CountHit(); return ServeUsers(ctx); });

        // ===== 去中心化 P2P 入站 / 查询端点 =====
        // v3.8.0 对齐：/api/mesh/fail|heartbeat 是**唯一**推送入口（旧 /api/fail|/api/heartbeat 已删，
        // 二者在 mesh 模式本就同处理）；POST / 保留兼容遗留 AgentPusher（按 body type 分发）。
        _pipeline.Add("POST", "/api/mesh/fail", ctx => { CountHit(); ServeMeshPush(ctx, "fail"); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/mesh/heartbeat", ctx => { CountHit(); ServeMeshPush(ctx, "heartbeat"); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/mesh/event", ctx => { CountHit(); ServeMeshEvent(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/mesh/summary", ctx => { CountHit(); ServeMeshSummary(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/mesh/fetch", ctx => { CountHit(); ServeMeshFetch(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/mesh/peers", ctx => { CountHit(); ServeMeshPeers(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/mesh/xml", ctx => { CountHit(); ServeMeshXml(ctx); return Task.CompletedTask; });
        // v3.5.0 旧版 GET /api/todos（跨机同步视图）已迁移至 /api/todos/sync，保留兼容但主路径让给 P5 维修待办
        _pipeline.Add("GET", "/api/todos/sync", ctx => { CountHit(); ServeTodos(ctx); return Task.CompletedTask; });

        // ===== Agent A: P4 源机检索（2026-08-28）=====
        // POST /api/mesh/query      聚合端扇出：并发查各机台 test_records 全量（含 PASS），结果聚合缓存 5 分钟，零膨胀
        // POST /api/mesh/query/local 本机执行：仅查本机 test_records（供扇出调用，防递归）
        // 安全：Handle 层 IsAuthenticated 已统一校验 agg_token/users；解析层 DTD Prohibit + LocalReadValidator 保持
        // B/C 代理请勿在此 region 内增改——如需新增请另起 region
        _pipeline.Add("POST", "/api/mesh/query", ctx => { CountHit(); return ServeMeshQuery(ctx); });
        _pipeline.Add("POST", "/api/mesh/query/local", ctx => { CountHit(); return ServeMeshQueryLocal(ctx); });

        // ===== Agent B: P5 维修/待办全功能（2026-08-28）=====
        // 维修看板 4 列状态机 + 待办 + 人员 + 导出，全部服务端化，口径复用同一 C# 逻辑（MaintenanceMeta/TodoGrouping 等）。
        // A/C 代理请勿在此 region 内增改——如需新增请另起 region 或协调
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
        // 兼容旧路径别名（部分前端可能用 /api/maintenance/export）
        _pipeline.Add("GET", "/api/maintenance/export", ctx => { CountHit(); ServeExportMaintenance(ctx); return Task.CompletedTask; });

        #region Agent C: P6 设备监控 + P2-A 服务化 + P7 数据拉取雏形（2026-08-28）— 单一 region，A/B 禁改
        // 协调：本 region 集中 Agent C 全部路由；新增请在此 region 内增改，避免与 A/B 的 P4/P5 region 冲突（见顶部注释）。
        // P6 设备上报（机台→聚合）
        _pipeline.Add("POST", "/api/mesh/info", ctx => { CountHit(); ServeMeshInfo(ctx); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/mesh/fctini", ctx => { CountHit(); ServeMeshFctIni(ctx); return Task.CompletedTask; });
        // P6 设备查看（聚合→看板，5s 缓存）
        _pipeline.Add("GET", "/api/devices", ctx => { CountHit(); ServeDevices(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/devices/*", ctx => { CountHit(); ServeDeviceWildcard(ctx); return Task.CompletedTask; });
        // P7 数据拉取/报告中心雏形（异步思想 + 趋势/分布）
        _pipeline.Add("POST", "/api/fetch", ctx => { CountHit(); return ServeFetchCreate(ctx); });
        _pipeline.Add("GET", "/api/fetch/jobs", ctx => { CountHit(); ServeFetchJobs(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/fetch/status", ctx => { CountHit(); ServeFetchStatus(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/trends", ctx => { CountHit(); ServeTrends(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/distribution", ctx => { CountHit(); ServeDistribution(ctx); return Task.CompletedTask; });
        // 兼容旧路径：/api/mesh/fetch 已有，去中心化对账用；/api/trends 等无 mesh 前缀亦可直达
        _pipeline.Add("GET", "/api/mesh/trends", ctx => { CountHit(); ServeTrends(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/mesh/distribution", ctx => { CountHit(); ServeDistribution(ctx); return Task.CompletedTask; });
        #endregion

        #region Lite-Fetch: 数据拉取完整 + 报告中心 + 程序日志路由（2026-08-28）— 单一 region，A/B/C 禁改
        // 约束：零第三方、单 exe、手写 OOXML、统一 CSV 注入防护与 BOM、按机台/时间段/型号筛选、进度可轮询、下载、报告摘要归档对比、日志登记时间轴参数diff
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
        // 兼容别名
        _pipeline.Add("GET", "/api/proc_log", ctx => { CountHit(); ServeProcLogList(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/proc_log/timeline", ctx => { CountHit(); ServeProcLogTimeline(ctx); return Task.CompletedTask; });
        #endregion

        #region Lite-Settings: 剩余前端设置与体验层（2026-08-28）— 单一 region，A/B/C 禁改
        // 本 region 集中 Lite 全部路由：用户“我的”布局/收藏、全局搜索、当前身份
        // 约束：零第三方、黑白红令牌、前后端双写、viewer可读/engineer写/admin管用户
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
        // 约束：原生 HTTP 直连，零第三方，单写者；MeshPusher per-peer 并发仍共享队列，(machine,seq)幂等；Gossiper 自适应 60/10 可观测；
        // 告警规则统一配置（device_alert_* + yield_alert_*）前端展示；overview 对比态基于已有 /api/distribution & /api/trends
        _pipeline.Add("GET", "/api/alerts/rules", ctx => { CountHit(); ServeAlertRules(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/alerts/history", ctx => { CountHit(); ServeAlertHistory(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/gossiper/status", ctx => { CountHit(); ServeGossiperStatus(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/compare/trends", ctx => { CountHit(); ServeCompareTrends(ctx); return Task.CompletedTask; });
        _pipeline.Add("GET", "/api/compare/distribution", ctx => { CountHit(); ServeCompareDistribution(ctx); return Task.CompletedTask; });
        #endregion

        #region Lite-Ops: 告警规则热更新（2026-08-28）— 单一 region，A/B/C/Lite-Fetch/Lite-Settings/Lite-Infra 禁改
        // v3.17.0 留的尾巴：改告警阈值要重启聚合服务（产线重启=中断采集）。
        // 约束：只改阈值类字段（device_alert_* + yield_alert_*）；AggAlertService 每轮轮询重新读
        // AppConfig.Instance，故改内存单例 + 原子写 config.json 即热生效，无需重启；
        // 阈值封顶校验防误配（disk 0~100000GB / cpu 0~100% / offline 0~10080min / yield 0~100%）；
        // admin 专属（改全局告警策略=高危操作）+ 审计落库；config 向后兼容（旧配置无 enabled 字段亦正确）。
        _pipeline.Add("PATCH", "/api/alerts/rules", ctx => { CountHit(); ServeAlertRulesPatch(ctx); return Task.CompletedTask; });
        _pipeline.Add("PUT", "/api/alerts/rules", ctx => { CountHit(); ServeAlertRulesPatch(ctx); return Task.CompletedTask; });
        _pipeline.Add("POST", "/api/alerts/rules", ctx => { CountHit(); ServeAlertRulesPatch(ctx); return Task.CompletedTask; });
        #endregion

        // 原写在 switch 之前的 /public/* 静态分支并入 pipeline（通配匹配保持原 IgnoreCase 语义）；
        // handler 内部重新归一化 path 后传给 ServePublicFile（与旧时代码等价）
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

    /// <summary>并发处理上限（M-5 加固）：超出直接 503 断连，防慢速连接耗尽线程池拖垮看板/推送。
    /// 64 对产线 7~10 台机台 + 几个浏览器绰绰有余。</summary>
    private static readonly SemaphoreSlim ConcurrencyGate = new(64, 64);
    // v3.10.0 P1-1 观测：并发超限被 503 断连的累计次数
    private static long _rejected503;

    /// <summary>并发超限 503 累计数（/api/metrics 观测）。</summary>
    public static long Rejected503Count => Interlocked.Read(ref _rejected503);

    /// <summary>阻塞 GetContext + 每请求 Task.Run 处理（accept 循环绝不被单个慢请求阻塞）；
    /// 并发超限时立即回 503 并断开（不进处理队列）。</summary>
    private void AcceptLoop()
    {
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = _listener!.GetContext(); }
            catch { break; }   // 监听器被 Stop/Close：GetContext 抛异常，正常退出
            if (!ConcurrencyGate.Wait(0))
            {
                Interlocked.Increment(ref _rejected503);   // v3.10.0 P1-1 观测
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

    // ==================== 单请求处理 ====================

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            using var resp = ctx.Response;

            // 鉴权（配置 agg_token 或存在 users 后生效）：POST 推送校验 X-Agg-Token 头；
            // GET 看板/API 校验头 / ?token= / Cookie 任一。agg_token 与 users.token 任一匹配即通过
            // （v3.8.0 P1 D3：users 三角色通道与旧 agg_token 通道并存），不匹配一律 403。
            // 未配置 agg_token 且无用户 = 宽松模式（保持 v3.5.x 未鉴权向后兼容）。
            // /api/login 豁免：未登录用户必须能到达登录端点（登录后拿 token 再走常规鉴权）。
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
            // 认证通过：下发/续期 HttpOnly Cookie（值=当前携带的 token，用户 token 登录不会拿到 agg_token 提权）
            // ⚠ v3.10.0 bugfix：鉴权态（配置了 agg_token **或** users 表非空）都要下发 Cookie——
            // 否则浏览器页面内 js/css 等静态资源请求不带 ?token= 参数，只靠 Cookie 续命；
            // 只按 agg_token 下发会在「agg_token 未配置 + 用户已建」时前端白屏（页面 200 但资源全 403）。
            if (_token.Length > 0 || _db.ListUsers().Count > 0) IssueTokenCookie(resp, ExtractToken(ctx));

            // 路由分发交给极简 pipeline（RoutePipeline）：归一化 path → 先精确后通配匹配 →
            // 未命中 404、方法不符 405、命中调 handler（命中前仍先 CountHit()）——与原 Handle() 大
            // switch 语义完全一致；原 switch 之前的 /public/* 静态分支已并入 pipeline 注册（见 RegisterRoutes()）。
            _pipeline.Handle(ctx);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 请求处理异常: {ex.Message}");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    private void CountHit() => Interlocked.Increment(ref _receivedCount);

    /// <summary>
    /// public/ 静态资源服务（v3.8.0 演示 / 前端模块化基础，纯新增）。
    /// 只读（仅 GET）；路径穿越防护：解析后必须仍位于 publicRoot 之内（reparse point 组件同样拒绝）；
    /// 默认入口 /public/ → public/index.html；MIME 白名单：html/css/js/json/svg/png/ico/txt。
    /// 静态页本身不再单独鉴权（配 agg_token 后整服务统一 403），页面内 API 请求仍走各自鉴权。
    /// </summary>
    private void ServePublicFile(HttpListenerContext ctx, string path)
    {
        try
        {
            var publicRoot = Path.GetFullPath(Path.Combine(AppConfig.BaseDir, "public"));
            var rel = path.Substring("/public".Length).TrimStart('/', '\\');
            if (rel.Length == 0) rel = "index.html";
            // 目录访问（如 /public/demo/）→ 落到该目录的 index.html
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
            // v3.10.0：静态资源统一 no-cache（前端迭代期，静态服务每次读盘即时生效；
            // 此前仅 HTML no-cache、CSS/JS 走 5 分钟缓存，改 JS 后浏览器旧缓存导致白屏难排查）。
            // 产线浏览器本地有磁盘缓存也可接受——服务端每次返回最新，代价只是多几次 304 级校验。
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.StatusCode = 200;
            using (var fs = File.OpenRead(full))
            {
                ctx.Response.ContentLength64 = fs.Length;
                fs.CopyTo(ctx.Response.OutputStream);
            }
            ctx.Response.OutputStream.Close();   // HttpListener 必须显式关闭输出流，客户端才能收到完整 body
        }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 静态资源服务失败 {path}: {ex.Message}");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    /// <summary>鉴权用 Cookie 名（M-3 加固：token 不再依赖 URL 逐跳传递）。</summary>
    internal const string TokenCookieName = "agg_token";

    /// <summary>从请求提取令牌：header X-Agg-Token / query token / Cookie 三路（与 Authorized 同源）。</summary>
    private static string ExtractToken(HttpListenerContext ctx)
    {
        var got = ctx.Request.Headers[HttpIngest.TokenHeader];
        if (string.IsNullOrEmpty(got)) got = ctx.Request.QueryString["token"] ?? ""; // 仅浏览器看板临时用，生产请用Header
        if (string.IsNullOrEmpty(got))
        {
            // Cookie 兜底（服务端在首次 ?token= 认证成功后下发 HttpOnly Cookie，
            // 之后翻页/下载链接无需再把 token 拼进 URL —— 不进浏览器历史/代理日志）
            var ck = ctx.Request.Cookies[TokenCookieName];
            if (ck != null) got = ck.Value ?? "";
        }
        return got ?? "";
    }

    /// <summary>鉴权检查：header X-Agg-Token / query token / Cookie 三路任一匹配即通过
    /// （聚合端未配置 token 时永不调用本方法）。比较用固定时间（L-1 防时序侧信道）。</summary>
    private bool Authorized(HttpListenerContext ctx)
        => FixedTimeEquals(ExtractToken(ctx), _token);

    /// <summary>
    /// 整服务鉴权（v3.8.0 P1 D3 扩展）：agg_token 或 users.token 任一匹配即通过；
    /// 未配置 agg_token 且无用户 = 宽松模式放行（保持 v3.5.x 未鉴权向后兼容）。
    /// 路由层再按 <see cref="RequireRole"/> 做角色分级（写/管理接口）。
    /// </summary>
    private bool IsAuthenticated(HttpListenerContext ctx)
    {
        if (_token.Length == 0 && _db.ListUsers().Count == 0) return true;   // 宽松模式
        var got = ExtractToken(ctx);
        if (!string.IsNullOrEmpty(got))
        {
            if (_token.Length > 0 && FixedTimeEquals(got, _token)) return true;
            if (_db.GetUserByToken(got) != null) return true;
        }
        return false;
    }

    /// <summary>
    /// 解析请求身份与角色（v3.8.0 P1 D3）：
    /// ① token 匹配 agg_token → (admin, "agg_token")，兼容现有机台推送与旧链接；
    /// ② token 匹配 users.token → 该用户 (role, name)；
    /// ③ 未配置 agg_token 且 users 表为空 → 宽松模式 (admin, "anonymous")（保持未鉴权向后兼容）；
    /// ④ 其余 → (null, null) 未授权（403）。
    /// </summary>
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

    /// <summary>角色分级：viewer &lt; engineer &lt; admin。未知角色按 0（拒绝）。</summary>
    private static int RoleLevel(string role) => role switch
    {
        "viewer" => 1,
        "engineer" => 2,
        "admin" => 3,
        _ => 0,
    };

    /// <summary>要求至少 minRole（viewer/engineer/admin）。不满足返回 403 并 false（调用方应立即 return）。</summary>
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

    /// <summary>常数时间字符串比较：先比长度（长度本身不泄密——token 定长 32hex），再按位异或累积。</summary>
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

    /// <summary>认证通过后向浏览器下发 token Cookie（HttpOnly+SameSite=Strict；同站 fetch/链接自动携带）。
    /// 值=当前请求携带的 token（v3.8.0 P1 D3：用户 token 登录不下发 agg_token，防止匿名提权为 admin）。
    /// 仅 GET 场景需要；失败静默（无 Cookie 能力客户端仍可用 header/?token= 访问）。
    /// ⚠ System.Net.Cookie 无 SameSite 属性（那是 ASP.NET 的 HttpCookie），手工拼 Set-Cookie 头。</summary>
    private void IssueTokenCookie(HttpListenerResponse resp, string tokenValue)
    {
        if (string.IsNullOrEmpty(tokenValue)) return;
        try { resp.Headers["Set-Cookie"] = $"{TokenCookieName}={tokenValue}; Path=/; HttpOnly; SameSite=Strict"; }
        catch { }
    }

    // ==================== GET 路由 ====================

    /// <summary>看板首页（v3.8.0 对齐）：public/ 模块化 SPA 存在时根路径 302 跳过去（保 ?token= 参数）；
    /// public/ 缺失（如 selftest 环境 / 精简部署）回退内嵌旧看板，保证任何环境根路径可用。</summary>
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

    /// <summary>旧内嵌单页看板（v3.8.0 起在 /legacy）：深色主题、看板|文件|设置三页签，占位符替换展示路径。</summary>
    private void ServeLegacyDashboard(HttpListenerContext ctx)
    {
        var page = DashboardHtml
            .Replace("%%SHARE_ROOT%%", WebUtility.HtmlEncode(_shareRoot))
            .Replace("%%DB_PATH%%", WebUtility.HtmlEncode(_db.DbPath))
            .Replace("%%RESULTS_ROOT%%", WebUtility.HtmlEncode(_resultsRoot))
            // JS 字符串上下文：反斜杠/引号必须转义，否则 "C:\Results" 里的 \R 会被 JS 当转义符吃掉；
            // `<` 与 `/` 一并转义，防 `</script><script>…` 跳出脚本上下文（存储型 XSS 防御纵深）
            .Replace("%%RESULTS_ROOT_JS%%", (_resultsRoot ?? "")
                .Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("<", "\\u003c").Replace("/", "\\u002f"));
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

    /// <summary>
    /// 在线 XML 报告查看（v3.8.0 MVP）：GET /api/xmlview?id=&lt;聚合库id&gt; →
    /// 按 failId 取报告内容（本机白名单或跨机 HTTP，见 <see cref="MeshReceiver.FetchXmlForFail"/>）
    /// → <see cref="XmlParser.ParseReportText"/> 解析 → <see cref="XmlReportHtml.Render"/> 渲染
    /// 等效本地查看器的 HTML 页面。鉴权已由 Handle 统一处理。
    /// v3.12.0 扩展（P4 源机检索联动）：支持 ?path=（任意机台任意产品，含 PASS）+ 可选 ?machine= 提示源机；
    /// 路径受 LocalReadValidator + DTD Prohibit 双重守护，跨机时并发扇出仍走 HttpClient + agg_token。
    /// </summary>
    private void ServeXmlView(HttpListenerContext ctx)
    {
        try
        {
            var q = ctx.Request.QueryString;
            // P4 扩展：?path= 分支（任意产品，含 PASS）—— 与 ?id= 分支同等渲染口径
            var pathParam = q["path"];
            if (!string.IsNullOrEmpty(pathParam))
            {
                var machineHint = q["machine"] ?? "";
                string? xml2 = null;
                // 本机白名单直读（D-T-L 验证同 /api/file）
                if (ResolveFile(pathParam, out _) == 200 && File.Exists(pathParam))
                {
                    try { xml2 = File.ReadAllText(pathParam, Encoding.UTF8); } catch { }
                }
                // 跨机拉取（源机 /api/file?path=，带 agg_token，peer 侧同样白名单校验）
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
                // 去中心化：本机白名单直读，或跨机 HTTP 从源机拉取
                xml = _mesh.Receiver.FetchXmlForFail(id);
            }
            else
            {
#pragma warning disable CS0618
                // 遗留中心模式：本地白名单读 xml_path（ResolveFile 校验同 /api/file）
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

    /// <summary>
    /// 跨机按路径拉取 XML（P4）：并发尝试各 peer 的 GET /api/file?path=（带 agg_token），
    /// 命中即返回文本；machineHint 非空时优先该机台对应 URL（若可映射），否则轮询全部。
    /// 供 /api/xmlview?path= 的跨机分支使用（与 FetchXmlForFail 的 id 分支互补）。
    /// </summary>
    private string? FetchRemoteXmlByPath(string machineHint, string path)
    {
        if (_mesh == null) return null;
        var urls = _mesh.PeerUrls;
        if (urls.Length == 0) return null;
        // 若有 machineHint，尝试把提示机台的 URL 放最前（通过已知的 PeerStatuses 映射 machine->url？暂用轮询，首个成功即回）
        // 简化：直接轮询全部，带 2s 超时
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

    /// <summary>设置读取：返回聚合端当前配置（管理后台「设置」页填充用）。
    /// 只读聚合相关字段；密码类字段（token）**不回显**，只给布尔「是否已配置」。</summary>
    private void ServeSettings(HttpListenerContext ctx)
    {
        try
        {
            var cfg = AppConfig.Instance;
            var payload = new Dictionary<string, object?>
            {
                // v3.8.0 端口对齐：headless（agg --web）实际监听 mesh_port（默认 8081），
                // 设置页必须暴露真实监听端口 _port；旧 agg_http_port 仅遗留中心模式（HttpIngest）用，不再展示
                ["mesh_port"] = _port,
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
        catch (BodyTooLargeException)
        {
            ctx.Response.StatusCode = 413;
            ctx.Response.KeepAlive = false;   // 立即断开：客户端可能仍在发大 body，不排空避免挂起
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
                // L-5 加固：只允许 https（http 会明文泄露 webhook 密钥，且可被当内网 SSRF 跳板）；
                // 空串=清除配置。非法值拒绝保存（400），与 FeishuNotifier 发送端策略同源。
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

            // 热生效：token 立即更新（本服务鉴权）；其余字段通知订阅方（AggCenterForm/Program 重启相关组件）
            bool needRestart = changed.Contains("mesh_port") || changed.Contains("agg_transport");
            if (changed.Contains("agg_token")) _token = cfg.AggToken;
            try { SettingsChanged?.Invoke(); } catch (Exception ex) { Logger.Warning($"[Web 服务] 设置变更回调异常: {ex.Message}"); }

            // v3.8.0 P1 D3：设置修改写审计（who/when/what）
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

    // ===== 域3 配置智能化 — 校验/推荐/回滚 API（v3.19.0） =====
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
    // ===== 域4 设备智能化 — 预测/巡检/变更 API（v3.19.0） =====
    private void ServeDevicesPredict(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        try
        {
            var preds = DevicePredictor.Predict(_db, 7);
            // 可选落库（避免每次重复写，限制频率可忽略）
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
    // ===== 域7 维修/待办智能 — 建议/流转 API（v3.19.0） =====
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

    /// <summary>机台状态列表：v3.5.0 去中心化下取 MeshReceiver peer 视图（含自身）；旧中心模式取 watcher 缓存。</summary>
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

    /// <summary>
    /// 良率日统计 JSON（v3.8.0 P1 D1，总纲 L1 /api/stats）：
    /// ?machine= 按机台过滤（空=全部）；?from= / ?to= 日期窗口（yyyyMMdd）；?max= 行数上限。
    /// 每行含 Yield=pass/total*100（total=0 记 100.0），口径与机台本地看板一致（FetchDailyStats 同源）。
    /// </summary>
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

    // ==================== v3.8.0 P1 D3：登录 / 状态 / 审计 / 用户管理 ====================

    /// <summary>
    /// 登录（v3.8.0 P1 D3）：POST JSON {name, password} → 校验 users 表 →
    /// 返回 {ok, name, role, token}（token=该用户静态令牌，前端存 localStorage 后随
    /// ?token=/X-Agg-Token 携带访问）。密码错误统一 401（不泄露用户是否存在）。成功写审计。
    /// </summary>
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

    /// <summary>
    /// 服务状态（v3.8.0 P1，总纲 L1 /api/status）：健康 + 版本 + 机台在线数 + FAIL 总数 +
    /// 今日 yld_daily 汇总（total/pass/良率）+ 鉴权模式 + 当前请求身份角色。
    /// </summary>
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

    /// <summary>
    /// 指标 JSON（v3.10.0 P1-1，架构方案 P1-A）：web 请求/503 拒绝、db 入库行数、
    /// receiver 组提交/接收/幂等忽略、pusher 队列/丢弃/发送成功失败、peer 状态。
    /// 数据全部来自既有 Interlocked 计数器，零额外开销；供脚本轮询与看板排障。
    /// </summary>
    private void ServeMetrics(HttpListenerContext ctx)
    {
        try
        {
            MeshPusher? pusher = _mesh?.Pusher;
            var recv = _mesh?.Receiver;
            var gossip = _mesh?.Gossiper;
            var uptime = _startedAt == default ? 0 : (long)(DateTime.Now - _startedAt).TotalSeconds;
            object? peers = _mesh?.PeerStatuses;
            // v3.17.0 Lite-Infra：per-peer 失败率 + gossiper 自适应间隔可观测
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

    /// <summary>审计查询（v3.8.0 P1 D3，admin）：GET /api/audit?limit= → 最近审计记录（who/when/what）。</summary>
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

    /// <summary>
    /// 用户管理（v3.8.0 P1 D3，admin）：
    /// GET → 用户列表（不含 pwd_hash）；POST {name,password,role} → 创建/更新（返回含新 token）；
    /// DELETE ?name= → 删除。全程写审计。
    /// </summary>
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

    /// <summary>FAIL 总数 JSON（分页用）：可选 machine/q 过滤，与 /api/fails 同条件。
    /// v3.5.3 走 5s 缓存计数（FailCountCached）：COUNT(*) 不再每页全表打一遍，读写分离后读路径也不持全局锁。</summary>
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
            // v3.8.0 P1 D3：导出写审计（先审计后响应 —— 响应返回时审计必须已落库，
            // 否则客户端收到 200 后立刻查审计会因竞态看到空）
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
        if (status == 403)
        {
            // v3.8.0 对齐：id= 场景本地白名单未命中（记录可能是源机路径）→ mesh 回退跨机拉取；
            // path= 是任意用户输入，保持严格白名单不回退。
            if (TryServeRemoteXml(ctx, idStr)) return;
            RespondText(ctx, 403, ForbiddenText);
            return;
        }

        byte[] data;
        try { data = File.ReadAllBytes(full!); }
        catch (Exception ex)
        {
            Logger.Warning($"[Web 服务] 读取报告文件失败 {full}: {ex.Message}");
            // v3.8.0 对齐：本地文件不可用（源机数据，本机只是占位路径）→ mesh 回退跨机拉取
            if (TryServeRemoteXml(ctx, idStr)) return;
            RespondText(ctx, 500, "读取文件失败");
            return;
        }

        // Content-Disposition inline + RFC 5987 UTF-8 文件名（中文文件名经浏览器正确解码）
        ctx.Response.Headers["Content-Disposition"] =
            $"inline; filename*=UTF-8''{Uri.EscapeDataString(Path.GetFileName(full!))}";
        Respond(ctx, 200, ContentTypeFor(full!), data);
    }

    /// <summary>mesh 模式回退：/api/file?id= 本地文件不可用时从源机实时拉取（与 /api/mesh/xml 同一逻辑）。
    /// 仅 id= 场景允许（记录源确定）；跨机拉取由源机自校验白名单，本机拉取仍走 LocalReadValidator，
    /// 无安全回退。成功写出响应返回 true，否则 false（调用方按原路径处理）。</summary>
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
            ctx.Response.KeepAlive = false;  // 立即断开，不排空大 body
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
            if (_mesh != null)
            {
                // v3.5.0 去中心化：经 MeshReceiver 入站（幂等入库到本地副本库）
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

    // ==================== v3.5.0 去中心化 P2P 端点 ====================

    /// <summary>邻居推来的 fail/heartbeat（v3.8.0 对齐后为**唯一**推送入口；与遗留中心模式共用
    /// <see cref="ServePush"/> 的 _mesh/_watcher 双分支，故不判 _mesh 为 null —— 遗留构造（watcher）下同样可用）。</summary>
    private void ServeMeshPush(HttpListenerContext ctx, string forcedType)
    {
        ServePush(ctx, forcedType);
    }

    /// <summary>邻居广播来的非 FAIL 事件（如全局待办变更）→ TodoSync 合并。</summary>
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

    /// <summary>Gossiper 对账：返回各机台在本副本库的最大 seq（machine→max_seq）。</summary>
    private void ServeMeshSummary(HttpListenerContext ctx)
    {
        if (_mesh == null) { ctx.Response.StatusCode = 404; return; }
        var max = _db.MaxSeqPerMachine();
        var machines = max.Select(kv => new Dictionary<string, object> { ["machine"] = kv.Key, ["max_seq"] = kv.Value }).ToList();
        var payload = new Dictionary<string, object> { ["machines"] = machines };
        Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
    }

    /// <summary>Gossiper 对账：按 (machine, from, to] 区间返回该机台 FAIL 增量（供邻居补推）。</summary>
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
            // 取本库该机台 (from, to] 区间的 fail 事件，原样序列化回邻居
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

    /// <summary>peer 拓扑视图（含自身）：供看板展示全产线机台在线状态与 FAIL 统计。</summary>
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

    /// <summary>按需拉取 XML 报告（轻量化：默认不冗余存储，点开时从源机/本地拉取）。</summary>
    private void ServeMeshXml(HttpListenerContext ctx)
    {
        if (_mesh == null) { ctx.Response.StatusCode = 404; return; }
        var idStr = ctx.Request.QueryString["id"] ?? "";
        if (!long.TryParse(idStr, out var id)) { ctx.Response.StatusCode = 400; return; }
        var xml = _mesh.Receiver.FetchXmlForFail(id);
        if (xml == null) { ctx.Response.StatusCode = 404; return; }
        Respond(ctx, 200, "application/xml; charset=utf-8", Encoding.UTF8.GetBytes(xml));
    }

    /// <summary>全局待办池视图：跨机同步的待办 owner/state（供看板「全产线待办」页）。</summary>
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

    /// <summary>
    /// 本机查询（POST /api/mesh/query/local）：仅查本机 Database test_records 全量（含 PASS），
    /// 有 sn/test_date/result 索引，3000 行内 &lt;100ms。返回 {ok, machine, results[], total}。
    /// 不扇出、不缓存，专供聚合端扇出调用（防递归）。
    /// 同步实现（阻塞），因 RoutePipeline 为 fire-and-forget（不 await），异步会导致响应未写完即被 Handle 释放。
    /// </summary>
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

    /// <summary>
    /// 聚合查询（POST /api/mesh/query）：并发查各机台 test_records 全量（含 PASS），结果聚合缓存 5 分钟，聚合端零膨胀。
    /// 流程：缓存键命中 → 直接返回；未命中 → 本机 QueryLocal + 并发 POST 各 peer /api/mesh/query/local（2.5s 超时，带 agg_token）→ 合并去重
    /// → 按 test_date 倒序 → Skip/Take 分页 → 序列化 + 写入缓存 → 返回 {ok, cached, total, results[], peers[]}。
    /// 并发模型：Task.WhenAll + Semaphore(64 已全局限流)，单机慢不拖累其他机台；结果 zero-copy 聚合，不写 AggDatabase。
    /// 同步阻塞实现：RoutePipeline 不 await（fire-and-forget），异步会导致空响应；此处用 GetAwaiter().GetResult() 阻塞至完成再返回。
    /// </summary>
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
            // 缓存命中：把 cached 标记翻为 true 后返回（字符串替换避免二次序列化开销）
            var hitJson = cachedJson.Replace("\"cached\":false", "\"cached\":true");
            // 若缓存体未含 cached 字段（旧版本），注入 header 提示
            if (hitJson == cachedJson) ctx.Response.Headers["X-Cache"] = "HIT";
            Respond(ctx, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(hitJson));
            return Task.CompletedTask;
        }

        // 未命中：扇出查询
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

        // 需要并发的 peer 数（按 req.Machine 过滤时，仍扇出全部但结果阶段再过滤——保证离线提示完整）
        // 同步阻塞：每 peer 任务内部用 GetAwaiter().GetResult()，外层 WhenAll 亦阻塞，避免 fire-and-forget 空响应
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
                            // 兼容小写字段（System.Text.Json 默认 PascalCase 序列化）
                            if (string.IsNullOrEmpty(it.Machine))
                                it.Machine = el.TryGetProperty("machine", out var vm2) ? vm2.GetString() ?? peer : peer;
                            list.Add(it);
                        }
                        catch { }
                    }
                }
                hit.Online = true;
                hit.Count = list.Count;
                // 推断 machine 名：取结果首条的 Machine，若空则用 peer URL
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

        // 若请求指定 machine，则过滤仅该机台（大小写不敏感，支持子串匹配以兼容 URL 场景）
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

    // ==================== 维修看板 API ====================

    /// <summary>
    /// GET /api/maintenance — 维修记录列表（服务端化，复用 MaintenanceMeta 状态机 legacy 兼容）。
    /// Query: ?status=（空=全部、unknown/open/in_progress/resolved，closed/investigating 自动归并）、limit、q=关键词（fail_item/fail_reason/resolver 模糊）。
    /// 权限：viewer 可读（IsAuthenticated 已前置，viewer 仅需过鉴权；未配置时宽松）。
    /// 返回：MaintenanceRecord[]（按 updated_at 倒序，已分页截断为 limit）。
    /// </summary>
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
            // 取全部后按关键词二次过滤（keep DB query simple + 兼容大小写不敏感 LIKE）
            if (string.IsNullOrEmpty(status))
                rows = _db.ListMaintenance("", limit + 200);
            else
                rows = _db.ListMaintenance(status, limit + 200);
            // legacy closed/investigating 已在 Normalize 归并，但历史库仍可能存 investigating，需额外合并
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

    /// <summary>
    /// GET /api/maintenance/counts — 各状态真实总数（不受 list LIMIT 影响），用于列头徽章。
    /// 复用 CountMaintenanceByStatus + Normalize 归并 legacy，返回 {open, in_progress, resolved, unknown, total}。
    /// </summary>
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
            // 确保 4 列都有值（0 补齐）
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

    /// <summary>
    /// POST /api/maintenance — 新建维修记录（单条或批量）。
    /// Body: 单条 {fail_item*, fail_reason, severity, status, resolver, resolution, notes, created_at, station_id,...}
    /// 或批量 {items: [string...], merge:bool, fail_reason, severity, status, resolver, resolution, notes}
    /// 逻辑：复用 MaintenanceForm.BatchResults 的 merge 规则（" / " 连接）。
    /// 权限：engineer+。
    /// </summary>
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
            // 批量分支：{items:[...], merge:true/false}
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
                // 公共字段
                var rec = ParseMaintenanceRecord(root, who);
                var results = new List<int>();
                if (merge)
                {
                    rec.FailItem = string.Join(" / ", items);
                    var id = _db.CreateMaintenance(rec);
                    results.Add(id);
                    // 多人自动登记
                    foreach (var n in ResolverUtil.Split(rec.Resolver)) try { _db.AddResolver(n); } catch { }
                    // 推送（如有状态）
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
            // 单条分支
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
        // severity key 规范化：若传入中文则转 key，若传入 key 则保留，否则默认 major
        if (rec.Severity != "critical" && rec.Severity != "major" && rec.Severity != "minor")
        {
            var zh = MaintenanceMeta.SeverityZhOf(rec.Severity);
            rec.Severity = MaintenanceMeta.SeverityKeyOf(string.IsNullOrEmpty(zh) ? "一般" : zh);
        }
        if (string.IsNullOrEmpty(rec.Status)) rec.Status = MaintenanceMeta.DefaultStatus;
        rec.Status = MaintenanceMeta.Normalize(rec.Status);
        return rec;
    }

    /// <summary>
    /// PATCH/PUT /api/maintenance — 全字段更新（编辑表单 / 拖拽改状态弹全字段确认）。
    /// Body 必须含 id，其余字段与 POST 同。状态变更会触发飞书推送（复用 FeishuNotifier）。
    /// 权限：engineer+。
    /// </summary>
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
            // 取请求字段覆盖现有（空串不覆盖除外，fail_item 必填则覆盖）
            string G(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            // 为兼容前端大小写，遍历属性名时不敏感
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
            // 规范化
            if (!string.IsNullOrEmpty(updated.Severity) && updated.Severity != "critical" && updated.Severity != "major" && updated.Severity != "minor")
                updated.Severity = MaintenanceMeta.SeverityKeyOf(MaintenanceMeta.SeverityZhOf(updated.Severity));
            updated.Status = MaintenanceMeta.Normalize(updated.Status);
            foreach (var n in ResolverUtil.Split(updated.Resolver)) try { _db.AddResolver(n); } catch { }
            if (!_db.UpdateMaintenance(updated)) { RespondText(ctx, 500, "update failed"); return Task.CompletedTask; }
            var (_, who) = ResolveRole(ctx);
            _db.LogAudit(who ?? "?", "maintenance.update", $"#{id} {fromStatus}->{updated.Status}");
            // 推送已在 UpdateMaintenance 触发事件，此处额外确保（事件内也会推，但 headless 需显式）
            // 事件已通过 AggDatabase.MaintenanceStatusChanged 触发，若 HeadlessService 未订阅则此处补推
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
                // 尝试从 body 取
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
            // 删除后若真实 FAIL 仍存在，其待办会随后通过 SyncTodoItems/前端提示回待办列（语义保留，自动）
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

    // ==================== 待办 API ====================

    /// <summary>
    /// GET /api/todos — 待办视图（合并大项、优先级 20/5、次数、合并变体 tooltip 数据）。
    /// Query: from=yyyyMMdd/to=yyyyMMdd（区间，空=全部永久）、limit。
    /// 逻辑复用 AggDatabase.ListTodoView（同 Database 口径）。
    /// </summary>
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
            // 若未传区间参数，则不限区间（全部永久保留），对应前端「全部」选项
            var hasRange = !string.IsNullOrWhiteSpace(q["from"]) || !string.IsNullOrWhiteSpace(q["to"]);
            if (!hasRange) { from = null; to = null; }
            // 同步最新 FAIL 到 todo（轻量幂等，与 MaintenanceBoard 行为一致）
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
        // 预留：当前待办由 SyncTodoItems 自动登记，不提供手动创建；返回 405 提示
        RespondText(ctx, 405, "todos are auto-synced from fails; use POST /api/todos/ack to confirm");
        await Task.CompletedTask;
    }

    /// <summary>
    /// POST /api/todos/ack — 确认待办（拖出待办列=确认，弹全字段确认）。
    /// Body: {todoId, station_id, fail_item, fail_reason, severity, status, resolver, resolution, notes, created_at}
    /// 逻辑复用 Database.AcknowledgeTodo（状态变更会推飞书）。
    /// </summary>
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

    // ==================== 维修人员 API ====================

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

    // ==================== 导出 ====================

    /// <summary>
    /// GET /api/export/maintenance — 12 列 xlsx/csv 导出，复用 MaintenanceExporter，CSV 注入防护 CWE-1236。
    /// Query: format=csv|xlsx（默认 xlsx）、status、q。
    /// 权限：viewer 可导出（与 FAIL 导出同级），但写 audit。
    /// </summary>
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
                // 复用 MaintenanceExporter.ExportCsv 的逻辑但直接走内存（同样 CWE-1236 防护与 BOM）
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

    // ---- 设备缓存（GET /api/devices 5s 缓存，同 FailCountCached 模式）----
    private readonly object _deviceCacheLock = new();
    private DateTime _deviceCacheAt = DateTime.MinValue;
    private byte[]? _deviceCacheBytes;

    private void InvalidateDeviceCache() { lock (_deviceCacheLock) { _deviceCacheAt = DateTime.MinValue; _deviceCacheBytes = null; } }

    // ---- POST /api/mesh/info（机台→聚合，L1 全量）----
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

    // ---- GET /api/devices（总览，5s 缓存，viewer 可读）----
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
            // 融合在线状态（PeerView 的 LastSeen 判定）+ FCT 型号概要
            var peers = _mesh?.PeerStatuses ?? new List<PeerStatusDto>();
            var onlineMap = peers.ToDictionary(p => p.Machine, p => p.Online, StringComparer.OrdinalIgnoreCase);
            // 若 device_info 缺失但 peer 存在，补一条占位（显示“未采集”）
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

    // ---- GET /api/devices/*（详情 / 采样 / fct）----
    private void ServeDeviceWildcard(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        var raw = ctx.Request.Url?.AbsolutePath ?? "";
        var path = raw.Length > 1 ? raw.TrimEnd('/') : raw;
        // path = /api/devices/{machine}[ /samples | /fct ]
        var rel = path.Substring("/api/devices".Length).Trim('/');
        if (string.IsNullOrEmpty(rel)) { ServeDevices(ctx); return; }
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var machine = parts[0];
        if (parts.Length == 1)
        {
            // 详情
            try
            {
                var info = _db.GetDeviceInfo(machine);
                if (info == null)
                {
                    // 尝试从 peer 视图补占位
                    var peers = _mesh?.PeerStatuses ?? new List<PeerStatusDto>();
                    var p = peers.FirstOrDefault(x => string.Equals(x.Machine, machine, StringComparison.OrdinalIgnoreCase));
                    if (p == null) { RespondText(ctx, 404, "device not found"); return; }
                    info = new DeviceInfoRow { Machine = p.Machine, LastSeen = p.LastHeartbeat ?? "", Online = p.Online };
                }
                else
                {
                    // 在线动态计算
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

    // ---- P7 完整：POST /api/fetch（真实 xlsx/zip 异步导出，含趋势/分布/热力，支持按机台/时间段/型号筛选，进度可轮询，完成后下载）----
    private async Task ServeFetchCreate(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException) { ctx.Response.StatusCode = 413; ctx.Response.KeepAlive = false; return; }
        try
        {
            // 完整导出：按机台/时间段/型号筛选，真实 xlsx/zip（含趋势/分布/热力），零第三方手写 OOXML
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
    // 约束：零第三方、黑白红令牌、统一 CSV 注入防护与 BOM、单 exe、手写 OOXML 经 FctShared.Xlsx，
    // 包含趋势/分布/热力数据、按机台/时间段/型号筛选、进度可轮询、完成后下载；报告检索预览摘要归档版本对比；proc_change_log 登记/时间轴/参数 diff。

    // ---- POST /api/fetch（完整导出，覆盖雏形：真实 xlsx/zip，含趋势/分布/热力）----
    // 注册已在 Agent C region（POST /api/fetch → ServeFetchCreate 雏形），此处增强：若请求带 format=zip/csv 或 pack=true 则走完整导出
    // 为避免重复注册，完整导出通过同一 handler 内分支（见 ServeFetchCreate 增强）；此处仅补充新路由

    private void ServeFetchDownload(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "viewer")) return;
        var id = ctx.Request.QueryString["id"] ?? ctx.Request.QueryString["job_id"] ?? "";
        if (string.IsNullOrWhiteSpace(id)) { RespondText(ctx, 400, "id required"); return; }
        var job = FetcherService.Get(id.Trim());
        if (job == null) { RespondText(ctx, 404, "job not found"); return; }
        if (job.Status != "done" || string.IsNullOrEmpty(job.FilePath) || !File.Exists(job.FilePath))
        {
            // 未完成时返回状态而非文件（避免 404 误导轮询）
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { ok = false, status = job.Status, progress = job.Progress, error = job.Error ?? "" }, JsonOpts));
            return;
        }
        try
        {
            var data = File.ReadAllBytes(job.FilePath);
            var ext = Path.GetExtension(job.FilePath).ToLowerInvariant();
            var mime = ext == ".zip" ? "application/zip" : ext == ".csv" ? "text/csv; charset=utf-8" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{Uri.EscapeDataString(job.FileName ?? Path.GetFileName(job.FilePath))}\"";
            // 审计：导出下载
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

    // ---- 报告中心：摘要/归档/版本对比 ----

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
            // 若提供 id，则从 agg_records 取元数据
            if (!string.IsNullOrEmpty(idStr) && long.TryParse(idStr, out var fid))
            {
                var row = _db.GetFailById(fid);
                if (row != null)
                {
                    machine = row.Machine; sn = row.Sn; model = row.Model; testDate = row.TestDate; result = row.Result; xmlPath = row.XmlPath;
                }
            }
            if (string.IsNullOrWhiteSpace(xmlPath)) { RespondText(ctx, 400, "xml_path or id required"); return Task.CompletedTask; }
            // 白名单校验或跨机拉取摘要
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
            // 落盘归档副本（data/report_archive/{machine}/{sn}_{id}.xml）
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
            // 对比 BATCH/PANEL/DUT/TEST 明细，保持与 XmlReportHtml 口径一致
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

    // ---- 程序日志：登记/时间轴/参数 diff ----

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
            // 允许 yyyyMMdd 或 yyyy-MM-dd 归一化为 yyyy-MM-dd HH:mm:ss
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
            // 时间轴按 changed_at 分组已由 SQL 倒序保证，前端直接渲染
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
    // 约束：黑白红零依赖、token 透传、viewer 可读、layout/favorites 前端 localStorage+后端双写、
    // 全局搜索空格分词跨 FAIL/维修/设备/良率聚合、跳转 hash 联动下钻、权限显隐后端已 403 前端再隐藏。

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
            // agg_token 匿名管理身份：无 users 行，返回 agg_token 特殊标记
            if (string.Equals(who, "agg_token", StringComparison.OrdinalIgnoreCase))
            {
                var payload = new { ok = true, name = "agg_token", role = "admin", token = _token, layout = (string?)null, favorites = (string?)null, isAggToken = true };
                Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
                return Task.CompletedTask;
            }
            // 宽松模式 anonymous
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
                // agg_token/anonymous 无 users 行：回 200 但不持久化（前端仅 localStorage）
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
            // 大小限制 64KB 防滥用
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
            // FAIL
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
            // 维修
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
            // 设备
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
            // 良率
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
            // 按相关度：命中 token 数已过滤全包含，次按类型稳定排序不额外加权
            var payload = new { ok = true, q, tokens, counts = new { fails = fails.Count, maintenance = maints.Count, devices = devs.Count, yields = yields.Count }, total = allResults.Count, results = new { fails, maintenance = maints, devices = devs, yields }, flat = allResults.Take(20).ToList() };
            Respond(ctx, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
        }
        catch (Exception ex) { Logger.Warning($"[Web 服务] search 失败: {ex.Message}"); RespondText(ctx, 500, "search failed"); }
    }

    #endregion

    #region Lite-Infra: 告警规则中心 + Gossiper自适应 + 多机台对比实现（2026-08-28）— 单一 region，A/B/C 禁改

    /// <summary>告警规则中心：统一返回 disk/cpu/offline/yield 四规则阈值与开关（config.json 可配，device_alert_* 已扩展到 yield_alert_*）。viewer 可读。</summary>
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

    /// <summary>告警历史：查询 alert_history（yield/disk/cpu/offline），支持 ?machine=&rule=&limit=&offset=。</summary>
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

    /// <summary>Gossiper 自适应状态可观测：当前间隔、原因、轮次、缺口。</summary>
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

    /// <summary>多机台趋势对比：在 /api/trends 基础上支持多机台并发对比（?machines=FCT1,FCT2&days=7），无需大改，复用 ReportService.GetTrend。</summary>
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
                // 单机或全量：直接透传 ReportService.GetTrend
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

    /// <summary>多机台分布对比：在 /api/distribution 基础上支持多机台（?machines=FCT1,FCT2&field=fail_reason&limit=10）。</summary>
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

    /// <summary>告警规则热更新：PATCH/PUT/POST /api/alerts/rules，admin 专属。
    /// 只改告警阈值字段（device_alert_disk_free_gb / device_alert_cpu_pct / device_alert_offline_minutes /
    /// yield_alert_yield_pct / yield_alert_enabled），改完原子写 config.json 并**立即热生效** ——
    /// AggAlertService 每轮轮询都重新读 AppConfig.Instance，所以不必重启聚合服务（重启会中断采集）。
    /// Body（各规则可选，只传要改的）：
    /// {disk:{enabled,threshold_gb}, cpu:{enabled,threshold_pct}, offline:{enabled,threshold_minutes}, yield:{enabled,threshold_pct}}
    /// 响应：{ok, changed:[字段名], rules:&lt;更新后快照&gt;}，前端可直接用 rules 回显。</summary>
    private void ServeAlertRulesPatch(HttpListenerContext ctx)
    {
        if (!RequireRole(ctx, "admin")) return;
        string body;
        try { body = ReadBody(ctx.Request); }
        catch (BodyTooLargeException)
        {
            ctx.Response.StatusCode = 413;
            ctx.Response.KeepAlive = false;   // 立即断开：客户端可能仍在发大 body，不排空避免挂起
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var cfg = AppConfig.Instance;
            var changed = new List<string>();

            // 语义约定（沿用 v3.17.0 config 口径，不新增字段、旧配置缺省可运行）：
            //   阈值 0 == 关闭（disk/cpu/offline 沿用此语义）；enabled=false → 阈值写 0；
            //   enabled=true 但阈值缺失/非正 → 回落出厂默认，免得"开了开关却还是关的"。
            //   yield 另有独立布尔 yield_alert_enabled（阈值保留，便于再次开启时不用重填）。

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
                // 上限 7 天：再大就失去"设备失联要尽快知道"的意义，且通常是误填
                if (v < 0 || v > 10080) { RespondText(ctx, 400, "offline 阈值需在 0~10080 分钟（0=关闭）"); return; }
                var iv = (int)Math.Round(v);
                if (iv != cfg.DeviceAlertOfflineMinutes) { cfg.DeviceAlertOfflineMinutes = iv; changed.Add("device_alert_offline_minutes"); }
            }
            if (root.TryGetProperty("yield", out var yEl) && yEl.ValueKind == JsonValueKind.Object)
            {
                bool on = ReadRuleEnabled(yEl, cfg.YieldAlertEnabled && cfg.YieldAlertYieldPct > 0);
                // yield 的开关是独立布尔（阈值保留），与 disk/cpu/offline 的「0=关闭」口径并存但各自自洽
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

            // 热生效：无需重建任何组件（AggAlertService 每轮重新读 AppConfig.Instance）；
            // 仍发一次设置变更通知，让 GUI/告警服务若持有派生状态可即时对齐。
            try { SettingsChanged?.Invoke(); } catch (Exception ex) { Logger.Warning($"[Web 服务] 告警规则变更回调异常: {ex.Message}"); }

            // 改全局告警策略属高危操作，必须留痕（与 settings.save 同级）
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

    /// <summary>读规则子对象的 enabled 字段；缺失时回落到「当前阈值 &gt; 0」这一既有语义 ——
    /// 老 config.json 里没有 enabled 字段，只翻阈值也必须能正常工作（向后兼容）。</summary>
    private static bool ReadRuleEnabled(JsonElement el, bool fallback)
    {
        if (el.TryGetProperty("enabled", out var e))
        {
            if (e.ValueKind == JsonValueKind.True) return true;
            if (e.ValueKind == JsonValueKind.False) return false;
        }
        return fallback;
    }

    /// <summary>读规则子对象的 threshold* 字段（四种命名都认，前端不必关心单位后缀）；
    /// 缺失或非正数时回落到出厂默认 —— 覆盖「只把开关打开、没填阈值」这一常见操作。</summary>
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

    /// <summary>把一条 AggFailRow 序列化为 MeshPusher 同格式的 fail 事件 JSON 字符串（供 Gossiper 增量拉取）。</summary>
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
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["machine"] = row.Machine, ["type"] = "fail", ["seq"] = row.Seq,
            ["ts"] = row.Ts, ["data"] = data,
        });
    }

    // ==================== 文件白名单（安全重点） ====================

    /// <summary>
    /// 文件白名单校验（?id= 查出的 xml_path 与 ?path= 直接传参共用）。
    /// 规则：
    ///   1. Path.GetFullPath 归一化（Windows 大小写不敏感、分隔符统一）；
    ///   2. 必须位于 resultsRoot 内（root 加尾分隔符后 StartsWith 比较，防 /root2 前缀误伤）；
    ///   3. 路径上任何目录组件不得是 reparse point（junction/symlink，M-6：防根内链接逃逸）；
    ///   4. 文件必须真实存在；
    ///   5. 例外白名单：{BaseDir}/logs/app.log 始终放行（resultsRoot 为空时唯一可用）。
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
        if (HasReparseComponent(full, root))   // M-6：junction/symlink 不放行
        {
            Logger.Warning($"[Web 服务] 拒绝含链接(reparse point)的路径: {full}");
            return 403;
        }
        if (!File.Exists(full)) return 403;
        return 200;
    }

    /// <summary>检查 root→full 路径上是否存在 reparse point 目录组件
    /// （GetFullPath 只做词法归一化，不解析 junction；SMB 扫描根里被塞一个指向 C:\ 的
    /// junction 时，仅靠前缀比较会被合法放行而逃逸根目录）。root 自身不检查。</summary>
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
                var attr = File.GetAttributes(cur);   // 不存在时抛异常 → 视为无链接（后续存在性检查兜底）
                if ((attr & FileAttributes.ReparsePoint) != 0) return true;
            }
            return false;
        }
        catch
        {
            return false;   // 组件不存在/无权限：交给既有的 Exists 检查处理
        }
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
            if (HasReparseComponent(full, root)) return 403;   // M-6：junction/symlink 不放行
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
        resp.Headers["X-Content-Type-Options"] = "nosniff";   // L-7：防 MIME 嗅探（/api/file 可输出用户可控内容）
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

    /// <summary>排空请求体剩余字节（超限/413 场景调用，保证 HttpListener 能正常发响应）。
    /// M-5 加固：最多再读 MaxDrainBytes（8MB），读不完就放弃——调用方已 KeepAlive=false 断连，
    /// 不给「声明 Content-Length: 10GB 再慢慢滴灌」的 DoS 留口子。</summary>
    private static void DrainBody(HttpListenerRequest req)
    {
        try
        {
            var buf = new byte[ReadChunk];
            long drained = 0;
            while (drained < MaxDrainBytes && req.InputStream.Read(buf, 0, buf.Length) > 0) drained += buf.Length;
        }
        catch { /* 连接已断则忽略 */ }
    }

    private const long MaxDrainBytes = 8 * 1024 * 1024;

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
