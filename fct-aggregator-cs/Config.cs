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
}
