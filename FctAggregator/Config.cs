using System.Collections.Generic;
using System.Text.Json;

namespace FctAggregator;

/// <summary>
/// 应用配置。从 exe 同级目录的 config.json 读取。
/// </summary>
public class AppConfig
{
    public string StationId { get; set; } = "";
    public string ResultsRoot { get; set; } = @"D:\Results";
    public string FctIniPath { get; set; } = @"C:\FTS\Apps\PEU\Cfg\FCT.ini";
    public string WebhookUrl { get; set; } = "";
    public bool SkipHistoricalScan { get; set; } = false;
    public string LogLevel { get; set; } = "INFO";
    /// <summary>Windows 原生桌面提示(不良报警)。config.json: desktop_notify。</summary>
    public bool DesktopNotify { get; set; } = true;
    /// <summary>两次桌面提示的最小间隔秒数。config.json: notify_min_interval_sec。</summary>
    public int NotifyMinIntervalSec { get; set; } = 15;
    /// <summary>
    /// 待办扫描窗口（天）：只把近 N 天的不良并入待办登记表，默认 30。
    /// 已登记的待办**永久保留**，不会因窗口滑动而消失。config.json: todo_scan_days。
    /// </summary>
    public int TodoScanDays { get; set; } = 30;
    /// <summary>机台聚合推送开关（Phase 1：FAIL 事件文件 + 心跳）。config.json: agg_enabled。</summary>
    public bool AggEnabled { get; set; } = false;
    /// <summary>共享目录根（如 \\192.168.x.x\ArgusAgg）。config.json: agg_share_root。</summary>
    public string AggShareRoot { get; set; } = "";
    /// <summary>聚合推送通道：smb=写共享目录（默认，兼容旧配置），http=POST 到聚合端。config.json: agg_transport。</summary>
    public string AggTransport { get; set; } = "smb";
    /// <summary>http 模式聚合端地址（如 http://192.168.1.100:8080/）。config.json: agg_http_url。</summary>
    public string AggHttpUrl { get; set; } = "";
    /// <summary>遗留中心模式 HTTP 接收端口（v3.5.0 前 HttpIngest/聚合看板 GUI 用；机台端不用管）。
    /// ⚠ headless（agg --web）与 P2P 节点实际监听 <see cref="MeshPort"/>，别把这两个搞混。
    /// config.json: agg_http_port。</summary>
    public int AggHttpPort { get; set; } = 8080;
    /// <summary>
    /// 聚合共享访问令牌（聚合端/机台端/看板三方共用，鉴权用）。config.json: agg_token。
    /// 空串=不启用鉴权（兼容现有部署）；配置后机台推送 / Web 看板 / API 一律校验，不匹配 403。
    /// </summary>
    public string AggToken { get; set; } = "";
    /// <summary>聚合端飞书群机器人 webhook（Phase 3 汇总/离线告警用）。config.json: agg_webhook_url。</summary>
    public string AggWebhookUrl { get; set; } = "";
    /// <summary>聚合汇总推送间隔（分钟，Phase 3）。config.json: agg_summary_minutes。</summary>
    public int AggSummaryMinutes { get; set; } = 60;
    /// <summary>
    /// 机台端聚合链路断连告警阈值（分钟）：连续推送失败持续超过该时长才推飞书
    /// 「聚合端不可达」告警（防网络抖动误报）；恢复时自动推「链路已恢复」。config.json: agg_fail_alert_minutes。
    /// </summary>
    public int AggFailAlertMinutes { get; set; } = 5;
    /// <summary>智能解析规则文件路径（v3.5.0）。空串=仅用内置默认解析器。config.json: parsers_path。</summary>
    public string ParsersPath { get; set; } = "parsers.json";
    /// <summary>P2P 本机监听端口（v3.5.0 去中心化）。config.json: mesh_port。</summary>
    public int MeshPort { get; set; } = 8081;
    /// <summary>邻居机台地址列表（v3.5.0 去中心化，静态发现）。config.json: peers（字符串数组）。</summary>
    public List<string> Peers { get; set; } = new();
    /// <summary>
    /// 副本库每日维护时刻（小时 0-23）：到点执行 wal_checkpoint(TRUNCATE)，库超阈值再 VACUUM。
    /// 默认凌晨 3 点低峰执行。config.json: db_maintenance_hour。
    /// </summary>
    public int DbMaintenanceHour { get; set; } = 3;
    /// <summary>
    /// 副本库 VACUUM 触发阈值（MB）：维护时库体积超过该值才 VACUUM（整理碎片/回收空间）。
    /// 0 = 从不 VACUUM（只 checkpoint）。config.json: db_vacuum_threshold_mb。
    /// </summary>
    public int DbVacuumThresholdMb { get; set; } = 512;
    /// <summary>聚合看板机台卡：显示心跳行。config.json: card_show_heartbeat。</summary>
    public bool CardShowHeartbeat { get; set; } = true;
    /// <summary>聚合看板机台卡：显示最近 FAIL 行。config.json: card_show_lastfail。</summary>
    public bool CardShowLastFail { get; set; } = true;
    /// <summary>聚合看板机台卡：显示待推队列行。config.json: card_show_queue。</summary>
    public bool CardShowQueue { get; set; } = true;
    /// <summary>聚合看板机台卡排序：name=名称 / fail=FAIL数降序 / online=在线优先。config.json: card_sort。</summary>
    public string CardSort { get; set; } = "name";
    /// <summary>聚合看板机台卡紧凑模式（隐藏副行、降低卡片高度）。config.json: card_compact。</summary>
    public bool CardCompact { get; set; } = false;
    /// <summary>
    /// 更新包检测目录（更新器用）：管理员把新的 Argus-v{ver}-update.zip 放进来，
    /// 程序启动时会检测并弹出更新提示。config.json: update_dir。
    /// 默认 data/updates（相对程序目录）；支持绝对路径。
    /// </summary>
    public string UpdateDir { get; set; } = "data/updates";
    // ===== P6 设备监控（总纲 Phase6，D1-D3，Agent C）=====
    /// <summary>机台端是否采集上报设备信息（默认 true）。config.json: device_info_enabled。</summary>
    public bool DeviceInfoEnabled { get; set; } = true;
    /// <summary>L1 详情上报周期秒（默认 300s=5 分钟）。config.json: device_info_interval_sec。</summary>
    public int DeviceInfoIntervalSec { get; set; } = 300;
    /// <summary>聚合端 device_samples 保留天数（默认 7 天，DbMaintenance 每日清理）。config.json: device_samples_retain_days。</summary>
    public int DeviceSamplesRetainDays { get; set; } = 7;
    /// <summary>磁盘剩余告警阈值 GB（0=关闭，默认 10）。config.json: device_alert_disk_free_gb。</summary>
    public double DeviceAlertDiskFreeGb { get; set; } = 10;
    /// <summary>CPU 持续超阈值告警百分比（0=关闭，默认 90）。config.json: device_alert_cpu_pct。</summary>
    public int DeviceAlertCpuPct { get; set; } = 90;
    /// <summary>离线告警阈值分钟（0=关闭，默认 5）。config.json: device_alert_offline_minutes。</summary>
    public int DeviceAlertOfflineMinutes { get; set; } = 5;
    // ===== P8 告警规则中心（yield 跌破统一规则，延续 device_alert_* 命名，新增 yield_alert_*）=====
    /// <summary>良率跌破告警阈值 %（0=关闭，默认 90）。config.json: yield_alert_yield_pct。</summary>
    public double YieldAlertYieldPct { get; set; } = 90;
    /// <summary>良率告警是否启用（默认 true，阈值 0 时仍关闭）。config.json: yield_alert_enabled。</summary>
    public bool YieldAlertEnabled { get; set; } = true;

    private static AppConfig? _instance;
    public static AppConfig Instance => _instance ??= Load();

    public static string GenerateRandomToken() => Guid.NewGuid().ToString("N");

    public static string BaseDir =>
        // 优先用程序集(DLL)所在目录; 避免用 dotnet.exe 路径(用 dotnet xxx.dll 启动时 ProcessPath 是 dotnet.exe)
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    public static AppConfig Load()
    {
        var path = Path.Combine(BaseDir, "config.json");
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var cfg = new AppConfig();
                if (root.TryGetProperty("station_id", out var v)) cfg.StationId = v.GetString() ?? "";
                if (root.TryGetProperty("results_root", out v)) cfg.ResultsRoot = v.GetString() ?? cfg.ResultsRoot;
                if (root.TryGetProperty("fct_ini_path", out v)) cfg.FctIniPath = v.GetString() ?? cfg.FctIniPath;
                if (root.TryGetProperty("webhook_url", out v)) cfg.WebhookUrl = v.GetString() ?? "";
                if (root.TryGetProperty("skip_historical_scan", out v)) cfg.SkipHistoricalScan = v.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("log_level", out v)) cfg.LogLevel = v.GetString() ?? "INFO";
                // 桌面提示：缺省开，只有显式写 false 才关
                if (root.TryGetProperty("desktop_notify", out v)) cfg.DesktopNotify = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("notify_min_interval_sec", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var sec) && sec >= 0)
                    cfg.NotifyMinIntervalSec = sec;
                if (root.TryGetProperty("todo_scan_days", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var days) && days >= 1)
                    cfg.TodoScanDays = days;
                // 机台聚合推送：缺省关，只有显式写 true 才开
                if (root.TryGetProperty("agg_enabled", out v)) cfg.AggEnabled = v.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("agg_share_root", out v)) cfg.AggShareRoot = v.GetString() ?? "";
                // 聚合推送通道：空/未知一律回落 smb（兼容旧配置）；http 才切直推
                if (root.TryGetProperty("agg_transport", out v))
                {
                    var t = (v.GetString() ?? "").Trim().ToLowerInvariant();
                    cfg.AggTransport = t == "http" ? "http" : "smb";
                }
                if (root.TryGetProperty("agg_http_url", out v)) cfg.AggHttpUrl = v.GetString() ?? "";
                cfg.AggHttpUrl = (cfg.AggHttpUrl ?? "").Trim();
                if (cfg.AggHttpUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    Logger.Warning($"[安全] agg_http_url 使用明文http，token将明文传输，建议改用https: {cfg.AggHttpUrl}");
                if (root.TryGetProperty("agg_http_port", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var port) && port >= 1 && port <= 65535)
                    cfg.AggHttpPort = port;
                if (root.TryGetProperty("agg_token", out v)) cfg.AggToken = v.GetString() ?? "";
                cfg.AggToken = (cfg.AggToken ?? "").Trim();
                if (root.TryGetProperty("agg_webhook_url", out v)) cfg.AggWebhookUrl = v.GetString() ?? "";
                if (root.TryGetProperty("agg_summary_minutes", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var m) && m >= 1)
                    cfg.AggSummaryMinutes = m;
                if (root.TryGetProperty("agg_fail_alert_minutes", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var fam) && fam >= 1)
                    cfg.AggFailAlertMinutes = fam;
                if (root.TryGetProperty("parsers_path", out v) && v.ValueKind == JsonValueKind.String)
                    cfg.ParsersPath = v.GetString() ?? "parsers.json";
                if (root.TryGetProperty("mesh_port", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var mp) && mp >= 1 && mp <= 65535)
                    cfg.MeshPort = mp;
                if (root.TryGetProperty("peers", out v) && v.ValueKind == JsonValueKind.Array)
                {
                    var peers = new List<string>();
                    foreach (var e in v.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.String) continue;
                        var p = (e.GetString() ?? "").Trim();
                        if (string.IsNullOrWhiteSpace(p)) continue;
                        if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                            Logger.Warning($"[安全] peer {p} 使用明文http，token将明文传输，建议改用https");
                        if (!Uri.TryCreate(p, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                        {
                            Logger.Warning($"[配置] peer地址非法已忽略: {p}");
                            continue;
                        }
                        peers.Add(p);
                    }
                    cfg.Peers = peers;
                }
                // v3.5.3 数据库日常维护：时刻 0-23，VACUUM 阈值 MB（负数当 0）
                if (root.TryGetProperty("db_maintenance_hour", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var mh) && mh >= 0 && mh <= 23)
                    cfg.DbMaintenanceHour = mh;
                if (root.TryGetProperty("db_vacuum_threshold_mb", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var vm) && vm >= 0)
                    cfg.DbVacuumThresholdMb = vm;
                // v3.8.0 聚合看板机台卡自定义
                if (root.TryGetProperty("card_show_heartbeat", out v)) cfg.CardShowHeartbeat = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("card_show_lastfail", out v)) cfg.CardShowLastFail = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("card_show_queue", out v)) cfg.CardShowQueue = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("card_sort", out v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = (v.GetString() ?? "").Trim();
                    if (s == "fail" || s == "online" || s == "name") cfg.CardSort = s;
                }
                if (root.TryGetProperty("card_compact", out v)) cfg.CardCompact = v.ValueKind == JsonValueKind.True;
                // v3.14.0 P6 设备监控（D1-D3）
                if (root.TryGetProperty("device_info_enabled", out v)) cfg.DeviceInfoEnabled = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("device_info_interval_sec", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var di) && di >= 30) cfg.DeviceInfoIntervalSec = di;
                if (root.TryGetProperty("device_samples_retain_days", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var dr) && dr >= 1) cfg.DeviceSamplesRetainDays = dr;
                if (root.TryGetProperty("device_alert_disk_free_gb", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var dg)) cfg.DeviceAlertDiskFreeGb = dg;
                if (root.TryGetProperty("device_alert_cpu_pct", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var dc) && dc >= 0) cfg.DeviceAlertCpuPct = dc;
                if (root.TryGetProperty("device_alert_offline_minutes", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var om) && om >= 0) cfg.DeviceAlertOfflineMinutes = om;
                // v3.17.0 P8 告警规则中心：良率跌破阈值（延续 device_alert_* 命名）
                if (root.TryGetProperty("yield_alert_yield_pct", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var yp)) cfg.YieldAlertYieldPct = yp;
                if (root.TryGetProperty("yield_alert_enabled", out v)) cfg.YieldAlertEnabled = v.ValueKind != JsonValueKind.False;
                // 若未配置token，生成随机token并警告（防空token全网开放）
                if (string.IsNullOrEmpty(cfg.AggToken))
                {
                    // 仅在非测试环境生成随机token提醒，实际不自动写回，由Save时处理
                    Logger.Warning("[安全] agg_token 未配置，当前为未鉴权模式，建议在config.json配置随机token");
                }
                return cfg;
            }
            else
            {
                Logger.Warning($"config.json 未找到(使用默认配置): {path}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"加载 config.json 失败: {ex.Message}");
        }
        return new AppConfig();
    }

    /// <summary>
    /// 把当前实例的聚合相关字段合并写回 config.json（保留磁盘上其他未知字段，不破坏手改的配置）。
    /// 写临时文件再原子改名；失败记 ERROR 返回 false（调用方决定是否提示）。
    /// </summary>
    public bool Save()
    {
        var path = Path.Combine(BaseDir, "config.json");
        try
        {
            // 读现有 JSON 保留未知字段；读不到/损坏则从空开始
            using var doc = JsonDocument.Parse(File.Exists(path) ? File.ReadAllText(path) : "{}");
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                var known = new HashSet<string>(StringComparer.Ordinal)
                {
                    "station_id", "results_root", "fct_ini_path", "webhook_url", "skip_historical_scan",
                    "log_level", "desktop_notify", "notify_min_interval_sec", "todo_scan_days",
                    "agg_enabled", "agg_share_root", "agg_transport", "agg_http_url", "agg_http_port",
                    "agg_token",                     "agg_webhook_url", "agg_summary_minutes", "agg_fail_alert_minutes", "parsers_path",
                    "mesh_port", "peers", "db_maintenance_hour", "db_vacuum_threshold_mb",
                    "card_show_heartbeat", "card_show_lastfail", "card_show_queue", "card_sort", "card_compact",
                    "device_info_enabled", "device_info_interval_sec", "device_samples_retain_days",
                    "device_alert_disk_free_gb", "device_alert_cpu_pct", "device_alert_offline_minutes",
                    "yield_alert_yield_pct", "yield_alert_enabled",
                };
                // 先写磁盘上的未知字段（保留原样），本类管理的字段随后覆盖
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (!known.Contains(prop.Name))
                        prop.WriteTo(writer);
                }
                writer.WriteString("station_id", StationId);
                writer.WriteString("results_root", ResultsRoot);
                writer.WriteString("fct_ini_path", FctIniPath);
                writer.WriteString("webhook_url", WebhookUrl);
                writer.WriteBoolean("skip_historical_scan", SkipHistoricalScan);
                writer.WriteString("log_level", LogLevel);
                writer.WriteBoolean("desktop_notify", DesktopNotify);
                writer.WriteNumber("notify_min_interval_sec", NotifyMinIntervalSec);
                writer.WriteNumber("todo_scan_days", TodoScanDays);
                writer.WriteBoolean("agg_enabled", AggEnabled);
                writer.WriteString("agg_share_root", AggShareRoot);
                writer.WriteString("agg_transport", AggTransport);
                writer.WriteString("agg_http_url", AggHttpUrl);
                writer.WriteNumber("agg_http_port", AggHttpPort);
                if (string.IsNullOrWhiteSpace(AggToken))
                {
                    AggToken = GenerateRandomToken();
                    Logger.Info($"[安全] agg_token 为空，已自动生成随机token: {AggToken.Substring(0,8)}...");
                }
                writer.WriteString("agg_token", AggToken);
                writer.WriteString("agg_webhook_url", AggWebhookUrl);
                writer.WriteNumber("agg_summary_minutes", AggSummaryMinutes);
                writer.WriteNumber("agg_fail_alert_minutes", AggFailAlertMinutes);
                writer.WriteString("parsers_path", ParsersPath);
                writer.WriteNumber("mesh_port", MeshPort);
                writer.WriteStartArray("peers");
                foreach (var p in Peers) writer.WriteStringValue(p);
                writer.WriteEndArray();
                writer.WriteNumber("db_maintenance_hour", DbMaintenanceHour);
                writer.WriteNumber("db_vacuum_threshold_mb", DbVacuumThresholdMb);
                writer.WriteBoolean("card_show_heartbeat", CardShowHeartbeat);
                writer.WriteBoolean("card_show_lastfail", CardShowLastFail);
                writer.WriteBoolean("card_show_queue", CardShowQueue);
                writer.WriteString("card_sort", CardSort);
                writer.WriteBoolean("card_compact", CardCompact);
                writer.WriteBoolean("device_info_enabled", DeviceInfoEnabled);
                writer.WriteNumber("device_info_interval_sec", DeviceInfoIntervalSec);
                writer.WriteNumber("device_samples_retain_days", DeviceSamplesRetainDays);
                writer.WriteNumber("device_alert_disk_free_gb", DeviceAlertDiskFreeGb);
                writer.WriteNumber("device_alert_cpu_pct", DeviceAlertCpuPct);
                writer.WriteNumber("device_alert_offline_minutes", DeviceAlertOfflineMinutes);
                writer.WriteNumber("yield_alert_yield_pct", YieldAlertYieldPct);
                writer.WriteBoolean("yield_alert_enabled", YieldAlertEnabled);
                writer.WriteEndObject();
            }
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, stream.ToArray());
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"保存 config.json 失败: {ex.Message}");
            return false;
        }
    }
}
