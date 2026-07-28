using System.Text.Json;

namespace FctAggregator;

/// <summary>
/// 应用配置。从 exe 同级目录的 config.json 读取。
/// </summary>
public class AppConfig
{
    public string StationId { get; set; } = "";
    public string ResultsRoot { get; set; } = @"D:\Results";
    public string FctIniPath { get; set; } = @"C:\FTS\Apps\XPT\Cfg\FCT.ini";
    public string WebhookUrl { get; set; } = "";
    public bool SkipHistoricalScan { get; set; } = false;
    public string LogLevel { get; set; } = "INFO";

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
