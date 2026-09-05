using System.Collections.Generic;
using System.Text.Json;

namespace FctAggregator;

public class AppConfig
{
    public string StationId { get; set; } = "";
    public string ResultsRoot { get; set; } = @"D:\Results";
    public string FctIniPath { get; set; } = @"C:\FTS\Apps\PEU\Cfg\FCT.ini";
    public string WebhookUrl { get; set; } = "";
    public bool SkipHistoricalScan { get; set; } = false;
    public string LogLevel { get; set; } = "INFO";
    public bool DesktopNotify { get; set; } = true;
    public int NotifyMinIntervalSec { get; set; } = 15;
    public int TodoScanDays { get; set; } = 30;
    public bool AggEnabled { get; set; } = false;
    public string AggShareRoot { get; set; } = "";
    public string AggTransport { get; set; } = "smb";
    public string AggHttpUrl { get; set; } = "";
    public int AggHttpPort { get; set; } = 8080;
    public string AggToken { get; set; } = "";
    public string AggWebhookUrl { get; set; } = "";
    public string FeishuBannerImgKey { get; set; } = "";
    public int AggSummaryMinutes { get; set; } = 60;
    public int AggFailAlertMinutes { get; set; } = 5;
    public bool TodoSpecMerge { get; set; } = true;
    public string ParsersPath { get; set; } = "parsers.json";
    public int MeshPort { get; set; } = 8081;
    public List<string> Peers { get; set; } = new();
    public int DbMaintenanceHour { get; set; } = 3;
    public int DbVacuumThresholdMb { get; set; } = 512;
    public bool CardShowHeartbeat { get; set; } = true;
    public bool CardShowLastFail { get; set; } = true;
    public bool CardShowQueue { get; set; } = true;
    public string CardSort { get; set; } = "name";
    public bool CardCompact { get; set; } = false;
    public string UpdateDir { get; set; } = "data/updates";
    public bool DeviceInfoEnabled { get; set; } = true;
    public int DeviceInfoIntervalSec { get; set; } = 300;
    public int DeviceSamplesRetainDays { get; set; } = 7;
    public double DeviceAlertDiskFreeGb { get; set; } = 10;
    public int DeviceAlertCpuPct { get; set; } = 90;
    public int DeviceAlertOfflineMinutes { get; set; } = 5;
    public double YieldAlertYieldPct { get; set; } = 90;
    public bool YieldAlertEnabled { get; set; } = true;
    public bool PredictReconcileEnabled { get; set; } = true;
    public int PredictReconcileHorizonDays { get; set; } = 14;
    public int PredictReconcileCronHour { get; set; } = 4;
    public bool PredictTuneEnabled { get; set; } = true;
    public int PredictTuneMinSamples { get; set; } = 30;
    public int PredictAccuracyRetainDays { get; set; } = 180;
    public bool YieldSeasonalityEnabled { get; set; } = false;
    public string YieldSeasonalityMode { get; set; } = "hourly";
    public double YieldSeasonalityEpsilon { get; set; } = 1.5;
    public int YieldSeasonalityDays { get; set; } = 28;
    public int YieldSeasonalityTrendWindow { get; set; } = 7;
    public double YieldSeasonalityMinSigma { get; set; } = 0.5;
    public bool HealthScoreEnabled { get; set; } = true;
    public double HealthWarnThreshold { get; set; } = 80;
    public double HealthCriticalThreshold { get; set; } = 50;
    public double HealthWeightCpu { get; set; } = 0.30;
    public double HealthWeightDisk { get; set; } = 0.30;
    public double HealthWeightMemory { get; set; } = 0.15;
    public double HealthWeightOffline { get; set; } = 0.25;
    public bool AutoUpdate { get; set; } = true;

    public bool LearnBaselineEnabled { get; set; } = false;
    public bool LearnResourceSamplingEnabled { get; set; } = false;
    public bool LearnPriorityEnabled { get; set; } = false;
    public bool LearnFailMergeEnabled { get; set; } = false;
    public string LearnFailMergeLevel { get; set; } = "signal";
    public int LearnBaselineWindowDays { get; set; } = 7;
    public double LearnBaselineSigma { get; set; } = 3.0;
    public int LearnBaselineMinSamples { get; set; } = 30;
    public int LearnGroupAlertMin { get; set; } = 3;
    public int LearnResourceRetentionDays { get; set; } = 14;

    private static AppConfig? _instance;
    public const string FallbackWebhookUrl = "";

    public static AppConfig Instance => _instance ??= Load();

    public static string GenerateRandomToken() => Guid.NewGuid().ToString("N");

    public static string BaseDir =>
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
                if (root.TryGetProperty("desktop_notify", out v)) cfg.DesktopNotify = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("notify_min_interval_sec", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var sec) && sec >= 0)
                    cfg.NotifyMinIntervalSec = sec;
                if (root.TryGetProperty("todo_scan_days", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var days) && days >= 1)
                    cfg.TodoScanDays = days;
                if (root.TryGetProperty("agg_enabled", out v)) cfg.AggEnabled = v.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("agg_share_root", out v)) cfg.AggShareRoot = v.GetString() ?? "";
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
                if (root.TryGetProperty("feishu_banner_img_key", out v)) cfg.FeishuBannerImgKey = (v.GetString() ?? "").Trim();
                if (root.TryGetProperty("agg_summary_minutes", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var m) && m >= 1)
                    cfg.AggSummaryMinutes = m;
                if (root.TryGetProperty("agg_fail_alert_minutes", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var fam) && fam >= 1)
                    cfg.AggFailAlertMinutes = fam;
                if (root.TryGetProperty("parsers_path", out v) && v.ValueKind == JsonValueKind.String)
                    cfg.ParsersPath = v.GetString() ?? "parsers.json";
                if (root.TryGetProperty("todo_spec_merge", out v) &&
                    (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                    cfg.TodoSpecMerge = v.GetBoolean();
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
                if (root.TryGetProperty("db_maintenance_hour", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var mh) && mh >= 0 && mh <= 23)
                    cfg.DbMaintenanceHour = mh;
                if (root.TryGetProperty("db_vacuum_threshold_mb", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var vm) && vm >= 0)
                    cfg.DbVacuumThresholdMb = vm;
                if (root.TryGetProperty("card_show_heartbeat", out v)) cfg.CardShowHeartbeat = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("card_show_lastfail", out v)) cfg.CardShowLastFail = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("card_show_queue", out v)) cfg.CardShowQueue = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("card_sort", out v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = (v.GetString() ?? "").Trim();
                    if (s == "fail" || s == "online" || s == "name") cfg.CardSort = s;
                }
                if (root.TryGetProperty("card_compact", out v)) cfg.CardCompact = v.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("device_info_enabled", out v)) cfg.DeviceInfoEnabled = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("device_info_interval_sec", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var di) && di >= 30) cfg.DeviceInfoIntervalSec = di;
                if (root.TryGetProperty("device_samples_retain_days", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var dr) && dr >= 1) cfg.DeviceSamplesRetainDays = dr;
                if (root.TryGetProperty("device_alert_disk_free_gb", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var dg)) cfg.DeviceAlertDiskFreeGb = dg;
                if (root.TryGetProperty("device_alert_cpu_pct", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var dc) && dc >= 0) cfg.DeviceAlertCpuPct = dc;
                if (root.TryGetProperty("device_alert_offline_minutes", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var om) && om >= 0) cfg.DeviceAlertOfflineMinutes = om;
                if (root.TryGetProperty("yield_alert_yield_pct", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var yp)) cfg.YieldAlertYieldPct = yp;
                if (root.TryGetProperty("yield_alert_enabled", out v)) cfg.YieldAlertEnabled = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("predict_reconcile_enabled", out v)) cfg.PredictReconcileEnabled = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("predict_reconcile_horizon_days", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var prh) && prh >= 1)
                    cfg.PredictReconcileHorizonDays = prh;
                if (root.TryGetProperty("predict_reconcile_cron_hour", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var prc) && prc >= 0 && prc <= 23)
                    cfg.PredictReconcileCronHour = prc;
                if (root.TryGetProperty("predict_tune_enabled", out v)) cfg.PredictTuneEnabled = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("predict_tune_min_samples", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var ptm) && ptm >= 1)
                    cfg.PredictTuneMinSamples = ptm;
                if (root.TryGetProperty("predict_accuracy_retain_days", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var par) && par >= 1)
                    cfg.PredictAccuracyRetainDays = par;
                if (root.TryGetProperty("yield_seasonality_enabled", out v)) cfg.YieldSeasonalityEnabled = v.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("yield_seasonality_mode", out v) && v.ValueKind == JsonValueKind.String)
                    cfg.YieldSeasonalityMode = v.GetString() ?? "hourly";
                if (root.TryGetProperty("yield_seasonality_epsilon", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var yse) && yse > 0)
                    cfg.YieldSeasonalityEpsilon = yse;
                if (root.TryGetProperty("yield_seasonality_days", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var ysd) && ysd >= 7)
                    cfg.YieldSeasonalityDays = ysd;
                if (root.TryGetProperty("yield_seasonality_trend_window", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var ystw) && ystw >= 1)
                    cfg.YieldSeasonalityTrendWindow = ystw;
                if (root.TryGetProperty("yield_seasonality_min_sigma", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var ysms) && ysms >= 0)
                    cfg.YieldSeasonalityMinSigma = ysms;
                if (root.TryGetProperty("health_score_enabled", out v)) cfg.HealthScoreEnabled = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("health_warn_threshold", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var hwt) && hwt >= 0 && hwt <= 100)
                    cfg.HealthWarnThreshold = hwt;
                if (root.TryGetProperty("health_critical_threshold", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var hct) && hct >= 0 && hct <= 100)
                    cfg.HealthCriticalThreshold = hct;
                if (root.TryGetProperty("health_weight_cpu", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var hwc) && hwc >= 0 && hwc <= 1)
                    cfg.HealthWeightCpu = hwc;
                if (root.TryGetProperty("health_weight_disk", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var hwd) && hwd >= 0 && hwd <= 1)
                    cfg.HealthWeightDisk = hwd;
                if (root.TryGetProperty("health_weight_memory", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var hwm) && hwm >= 0 && hwm <= 1)
                    cfg.HealthWeightMemory = hwm;
                if (root.TryGetProperty("health_weight_offline", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var hwo) && hwo >= 0 && hwo <= 1)
                    cfg.HealthWeightOffline = hwo;
                if (root.TryGetProperty("auto_update", out v)) cfg.AutoUpdate = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("learn_baseline_enabled", out v)) cfg.LearnBaselineEnabled = v.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("learn_resource_sampling_enabled", out v)) cfg.LearnResourceSamplingEnabled = v.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("learn_priority_enabled", out v)) cfg.LearnPriorityEnabled = v.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("learn_fail_merge_enabled", out v)) cfg.LearnFailMergeEnabled = v.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("learn_fail_merge_level", out v) && v.ValueKind == JsonValueKind.String)
                {
                    var lvl = (v.GetString() ?? "signal").Trim().ToLowerInvariant();
                    if (lvl == "off" || lvl == "signal" || lvl == "section") cfg.LearnFailMergeLevel = lvl;
                }
                if (root.TryGetProperty("learn_baseline_window_days", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var lbw) && lbw >= 1) cfg.LearnBaselineWindowDays = lbw;
                if (root.TryGetProperty("learn_baseline_sigma", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var lbs) && lbs > 0) cfg.LearnBaselineSigma = lbs;
                if (root.TryGetProperty("learn_baseline_min_samples", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var lbm) && lbm >= 1) cfg.LearnBaselineMinSamples = lbm;
                if (root.TryGetProperty("learn_group_alert_min", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var lga) && lga >= 1) cfg.LearnGroupAlertMin = lga;
                if (root.TryGetProperty("learn_resource_retention_days", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var lrr) && lrr >= 1) cfg.LearnResourceRetentionDays = lrr;
                if (string.IsNullOrEmpty(cfg.AggToken))
                {
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

    public bool Save()
    {
        var path = Path.Combine(BaseDir, "config.json");
        try
        {
            if (File.Exists(path))
            {
                var bakDir = Path.Combine(BaseDir, "data", "config_backups");
                Directory.CreateDirectory(bakDir);
                var bak = Path.Combine(bakDir, $"config_backup_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N[..6]}.json");
                File.Copy(path, bak);
                var olds = Directory.GetFiles(bakDir, "config_backup_*.json").OrderByDescending(f=>f).ToList();
                foreach (var f2 in olds.Skip(20)) try{ File.Delete(f2);}catch{}
            }
        } catch {}
        try
        {
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
                    "agg_token",                     "agg_webhook_url", "feishu_banner_img_key", "agg_summary_minutes", "agg_fail_alert_minutes", "parsers_path",
                    "todo_spec_merge",
                    "mesh_port", "peers", "db_maintenance_hour", "db_vacuum_threshold_mb",
                    "card_show_heartbeat", "card_show_lastfail", "card_show_queue", "card_sort", "card_compact",
                    "device_info_enabled", "device_info_interval_sec", "device_samples_retain_days",
                    "device_alert_disk_free_gb", "device_alert_cpu_pct", "device_alert_offline_minutes",
                    "yield_alert_yield_pct", "yield_alert_enabled",
                    "predict_reconcile_enabled", "predict_reconcile_horizon_days", "predict_reconcile_cron_hour",
                    "predict_tune_enabled", "predict_tune_min_samples", "predict_accuracy_retain_days",
                    "yield_seasonality_enabled", "yield_seasonality_mode", "yield_seasonality_epsilon",
                    "yield_seasonality_days", "yield_seasonality_trend_window", "yield_seasonality_min_sigma",
                    "health_score_enabled", "health_warn_threshold", "health_critical_threshold",
                    "health_weight_cpu", "health_weight_disk", "health_weight_memory", "health_weight_offline",
                    "auto_update",
                    "learn_baseline_enabled", "learn_resource_sampling_enabled", "learn_priority_enabled",
                    "learn_fail_merge_enabled", "learn_fail_merge_level", "learn_baseline_window_days",
                    "learn_baseline_sigma", "learn_baseline_min_samples", "learn_group_alert_min",
                    "learn_resource_retention_days",
                };
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
                writer.WriteString("feishu_banner_img_key", FeishuBannerImgKey);
                writer.WriteNumber("agg_summary_minutes", AggSummaryMinutes);
                writer.WriteNumber("agg_fail_alert_minutes", AggFailAlertMinutes);
                writer.WriteString("parsers_path", ParsersPath);
                writer.WriteBoolean("todo_spec_merge", TodoSpecMerge);
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
                writer.WriteBoolean("predict_reconcile_enabled", PredictReconcileEnabled);
                writer.WriteNumber("predict_reconcile_horizon_days", PredictReconcileHorizonDays);
                writer.WriteNumber("predict_reconcile_cron_hour", PredictReconcileCronHour);
                writer.WriteBoolean("predict_tune_enabled", PredictTuneEnabled);
                writer.WriteNumber("predict_tune_min_samples", PredictTuneMinSamples);
                writer.WriteNumber("predict_accuracy_retain_days", PredictAccuracyRetainDays);
                writer.WriteBoolean("yield_seasonality_enabled", YieldSeasonalityEnabled);
                writer.WriteString("yield_seasonality_mode", YieldSeasonalityMode);
                writer.WriteNumber("yield_seasonality_epsilon", YieldSeasonalityEpsilon);
                writer.WriteNumber("yield_seasonality_days", YieldSeasonalityDays);
                writer.WriteNumber("yield_seasonality_trend_window", YieldSeasonalityTrendWindow);
                writer.WriteNumber("yield_seasonality_min_sigma", YieldSeasonalityMinSigma);
                writer.WriteBoolean("health_score_enabled", HealthScoreEnabled);
                writer.WriteNumber("health_warn_threshold", HealthWarnThreshold);
                writer.WriteNumber("health_critical_threshold", HealthCriticalThreshold);
                writer.WriteNumber("health_weight_cpu", HealthWeightCpu);
                writer.WriteNumber("health_weight_disk", HealthWeightDisk);
                writer.WriteNumber("health_weight_memory", HealthWeightMemory);
                writer.WriteNumber("health_weight_offline", HealthWeightOffline);
                writer.WriteBoolean("auto_update", AutoUpdate);
                writer.WriteBoolean("learn_baseline_enabled", LearnBaselineEnabled);
                writer.WriteBoolean("learn_resource_sampling_enabled", LearnResourceSamplingEnabled);
                writer.WriteBoolean("learn_priority_enabled", LearnPriorityEnabled);
                writer.WriteBoolean("learn_fail_merge_enabled", LearnFailMergeEnabled);
                writer.WriteString("learn_fail_merge_level", LearnFailMergeLevel);
                writer.WriteNumber("learn_baseline_window_days", LearnBaselineWindowDays);
                writer.WriteNumber("learn_baseline_sigma", LearnBaselineSigma);
                writer.WriteNumber("learn_baseline_min_samples", LearnBaselineMinSamples);
                writer.WriteNumber("learn_group_alert_min", LearnGroupAlertMin);
                writer.WriteNumber("learn_resource_retention_days", LearnResourceRetentionDays);
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
    public List<string> Validate() => ConfigValidator.Validate(this);

    public static List<string> ValidateCurrent() => ConfigValidator.Validate(Load());

    public static List<string> ListBackups(int take = 20)
    {
        try
        {
            var dir = Path.Combine(BaseDir, "data", "config_backups");
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.GetFiles(dir, "config_backup_*.json").OrderByDescending(f=>f).Take(take).ToList();
        } catch { return new List<string>(); }
    }

    public static bool Rollback(string? backupPath = null)
    {
        try
        {
            var dir = Path.Combine(BaseDir, "data", "config_backups");
            if (string.IsNullOrEmpty(backupPath))
                backupPath = Directory.GetFiles(dir, "config_backup_*.json").OrderByDescending(f=>f).FirstOrDefault();
            if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath)) return false;
            var dest = Path.Combine(BaseDir, "config.json");
            File.Copy(backupPath, dest, overwrite:true);
            _instance = null;
            Logger.Info($"[配置] 已回滚到 {Path.GetFileName(backupPath)}");
            return true;
        } catch (Exception ex) { Logger.Error($"[配置] 回滚失败: {ex.Message}"); return false; }
    }
}
