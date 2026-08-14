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
    /// <summary>聚合端 HTTP 监听端口（聚合端用，机台端不用管）。config.json: agg_http_port。</summary>
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

    private static AppConfig? _instance;
    public static AppConfig Instance => _instance ??= Load();

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
                if (root.TryGetProperty("agg_http_port", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var port) && port >= 1 && port <= 65535)
                    cfg.AggHttpPort = port;
                if (root.TryGetProperty("agg_token", out v)) cfg.AggToken = v.GetString() ?? "";
                if (root.TryGetProperty("agg_webhook_url", out v)) cfg.AggWebhookUrl = v.GetString() ?? "";
                if (root.TryGetProperty("agg_summary_minutes", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var m) && m >= 1)
                    cfg.AggSummaryMinutes = m;
                if (root.TryGetProperty("agg_fail_alert_minutes", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var fam) && fam >= 1)
                    cfg.AggFailAlertMinutes = fam;
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
                    "agg_token", "agg_webhook_url", "agg_summary_minutes", "agg_fail_alert_minutes",
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
                writer.WriteString("agg_token", AggToken);
                writer.WriteString("agg_webhook_url", AggWebhookUrl);
                writer.WriteNumber("agg_summary_minutes", AggSummaryMinutes);
                writer.WriteNumber("agg_fail_alert_minutes", AggFailAlertMinutes);
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
