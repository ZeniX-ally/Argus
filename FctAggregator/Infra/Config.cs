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
    public string FeishuBannerImgKey { get; set; } = "";
    /// <summary>按抬头颜色覆盖头图。留空则回退 <see cref="FeishuBannerImgKey"/>。</summary>
    public string FeishuBannerImgKeyRed { get; set; } = "";
    public string FeishuBannerImgKeyOrange { get; set; } = "";
    public string FeishuBannerImgKeyBlue { get; set; } = "";
    public string FeishuBannerImgKeyGreen { get; set; } = "";
    public string FeishuBannerImgKeyYellow { get; set; } = "";
    /// <summary>FAIL 告警合并：窗口内多条 FAIL 合成一张卡（治批量不良刷屏）。关闭则每条立即单独推送。</summary>
    public bool FeishuFailMergeEnabled { get; set; } = true;
    /// <summary>合并窗口（秒）：从第一条 FAIL 入队起算的固定窗口，不因后续入队顺延。范围 10~3600。</summary>
    public int FeishuFailMergeWindowSec { get; set; } = 60;
    /// <summary>攒满多少条 FAIL 立即冲刷（不等窗口）。范围 1~200。</summary>
    public int FeishuFailMergeMax { get; set; } = 20;
    /// <summary>采集异常告警（漏采/解析失败堆积/重试队列积压）总开关。</summary>
    public bool FeishuCollectAlertEnabled { get; set; } = true;
    /// <summary>采集异常统计窗口（分钟）。范围 5~1440。</summary>
    public int FeishuCollectAlertWindowMin { get; set; } = 60;
    /// <summary>窗口内异常解析失败条数阈值（≤0 关闭该维度）。</summary>
    public int FeishuCollectAlertParseMin { get; set; } = 10;
    /// <summary>重试队列积压条数阈值（≤0 关闭该维度）。</summary>
    public int FeishuCollectAlertRetryMin { get; set; } = 20;
    /// <summary>同一采集告警的最短间隔（分钟），防持续异常刷屏。≤0 不节流。</summary>
    public int FeishuCollectAlertThrottleMin { get; set; } = 60;
    /// <summary>每日运行摘要卡（采集量/良率/Top失败项/库健康分/备份状态）。</summary>
    public bool FeishuDailySummaryEnabled { get; set; } = true;
    /// <summary>章节群挂告警（同一章节 N 个不同信号同时挂 → 系统性故障）。阈值复用 learn_group_alert_min。</summary>
    public bool FeishuGroupAlertEnabled { get; set; } = true;
    /// <summary>正常态偏离告警（PASS 但测量值偏离已学正常态 → 提前预警）。</summary>
    public bool FeishuDeviationAlertEnabled { get; set; } = true;
    /// <summary>偏离告警的分数下限（0~100）。低于此分只落库不推送。</summary>
    public int FeishuDeviationMinScore { get; set; } = 90;
    /// <summary>偏离告警单日推送上限（防预警变噪音）。</summary>
    public int FeishuDeviationMaxPerDay { get; set; } = 5;
    /// <summary>存储健康告警（库健康分低于阈值时推，防「库涨到 10GB 才被发现」）。</summary>
    public bool FeishuStorageAlertEnabled { get; set; } = true;
    /// <summary>存储健康告警的分数下限（0~100）。</summary>
    public int FeishuStorageAlertScore { get; set; } = 60;
    /// <summary>待办超期未闭环告警（每天最多一张汇总卡）。</summary>
    public bool FeishuTodoOverdueEnabled { get; set; } = true;
    /// <summary>待办超期判定天数（未完成态停留 ≥N 天）。</summary>
    public int FeishuTodoOverdueDays { get; set; } = 3;
    /// <summary>待办超期告警的严重度门槛：critical / major / minor（含及以上）。</summary>
    public string FeishuTodoOverdueMinSeverity { get; set; } = "critical";
    public bool TodoSpecMerge { get; set; } = true;
    public string ParsersPath { get; set; } = "parsers.json";
    public int DbMaintenanceHour { get; set; } = 3;
    /// <summary>自动存储优化（轻量巡检 + 凌晨深度清理/压缩）。</summary>
    public bool DbStorageAutoOptimize { get; set; } = true;
    /// <summary>轻量巡检间隔（小时）：WAL 合并 + 过期日志清理 + 健康评分。</summary>
    public int DbStorageMonitorHours { get; set; } = 4;
    /// <summary>库文件超过该大小（MB）或空洞率高时触发 VACUUM。</summary>
    public int DbVacuumThresholdMb { get; set; } = 500;
    /// <summary>parse_failure_log 保留天数。</summary>
    public int DbParseFailureRetentionDays { get; set; } = 30;

    /// <summary>FCT 测试程序根目录（默认 C:\FTS）。</summary>
    public string FctProgramSourceRoot { get; set; } = @"C:\FTS";
    /// <summary>测试程序压缩包输出目录（默认 D:\backup）。</summary>
    public string FctProgramBackupDir { get; set; } = @"D:\backup";
    public bool FctProgramBackupEnabled { get; set; } = true;
    /// <summary>磁盘上保留的 zip 份数（数据库记录永久保留）。</summary>
    public int FctProgramBackupKeep { get; set; } = 10;

    public string UpdateDir { get; set; } = "data/updates";
    /// <summary>只读更新源（共享文件夹/UNC 盘），非空时替代 UpdateDir 供扫描，暂存/备份仍落本机 UpdateDir。</summary>
    public string UpdateSource { get; set; } = "";
    public bool AutoUpdate { get; set; } = true;

    public bool LearnBaselineEnabled { get; set; } = false;
    public bool LearnResourceSamplingEnabled { get; set; } = false;
    public bool LearnPriorityEnabled { get; set; } = false;
    public bool LearnFailMergeEnabled { get; set; } = false;
    public string LearnFailMergeLevel { get; set; } = "signal";
    public int LearnBaselineWindowDays { get; set; } = 7;
    public double LearnBaselineSigma { get; set; } = 3.0;
    public int LearnBaselineMinSamples { get; set; } = 30;
    public bool LearnNormalEnabled { get; set; } = true;
    public int LearnNormalMinSamples { get; set; } = 30;
    public int LearnNormalStaleDays { get; set; } = 14;
    public int LearnNormalVoteSignals { get; set; } = 3;
    public int LearnNormalEventScore { get; set; } = 75;
    public int LearnNormalMaxEventsPerDay { get; set; } = 10;
    public int LearnGroupAlertMin { get; set; } = 3;
    public int LearnResourceRetentionDays { get; set; } = 14;

    // 规格06 多元深分析（analyze_*）
    public bool AnalyzeCollectPass { get; set; } = true;
    public int AnalyzeMaxFileKb { get; set; } = 2048;
    public int AnalyzeMaxTestsPerFile { get; set; } = 1000;
    public bool AnalyzeDriftEnabled { get; set; } = false;
    public bool AnalyzeCpkEnabled { get; set; } = false;
    public int AnalyzeWindowDays { get; set; } = 7;
    public int AnalyzeMinSamples { get; set; } = 30;
    public double AnalyzeDriftSigma { get; set; } = 3.0;
    public double AnalyzeDriftWarnRatio { get; set; } = 1.0;
    public int AnalyzeMeasureRetentionDays { get; set; } = 90;
    public int AnalyzeFailItemRetentionDays { get; set; } = 90;
    public bool AnalyzeTdmsEnabled { get; set; } = true;
    public string TdmsRoot { get; set; } = "";
    public int AnalyzeTdmsMaxKb { get; set; } = 65536;
    public int AnalyzeTdmsMaxChannels { get; set; } = 400;
    public bool AnalyzeFailAttrEnabled { get; set; } = true;
    public bool AnalyzeFixtureEnabled { get; set; } = false;
    public int AnalyzeAttrMinBucket { get; set; } = 20;
    public double AnalyzeAttrDevRatio { get; set; } = 1.5;
    public double AnalyzeEffectD { get; set; } = 0.8;

    private static AppConfig? _instance;
    public const string FallbackWebhookUrl = "";

    public static AppConfig Instance => _instance ??= Load();

    /// <summary>飞书头图：该颜色有 key 就用它，否则用 <see cref="FeishuBannerImgKey"/>。都空则这张卡不带头图。</summary>
    public string BannerImgKeyFor(string? template)
    {
        var specific = template switch
        {
            "red" => FeishuBannerImgKeyRed,
            "orange" => FeishuBannerImgKeyOrange,
            "blue" => FeishuBannerImgKeyBlue,
            "green" => FeishuBannerImgKeyGreen,
            "yellow" => FeishuBannerImgKeyYellow,
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(specific)) return specific.Trim();
        return (FeishuBannerImgKey ?? "").Trim();
    }

    public static string BaseDir =>
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>安全读取字符串键：JSON 值不是字符串（如数字/布尔）时返回 null（保留该键默认值），
    /// 避免单个键类型错误抛异常导致整份配置静默回落默认。</summary>
    private static string? GetStringSafe(JsonElement v)
        => v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static AppConfig Load(bool allowBackupRestore = true)
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
                if (root.TryGetProperty("station_id", out var v)) cfg.StationId = GetStringSafe(v) ?? "";
                if (root.TryGetProperty("results_root", out v)) cfg.ResultsRoot = GetStringSafe(v) ?? cfg.ResultsRoot;
                if (root.TryGetProperty("fct_ini_path", out v)) cfg.FctIniPath = GetStringSafe(v) ?? cfg.FctIniPath;
                // 审计修复：URL 型键里唯一没做 Trim 的——前后带空格/换行（复制粘贴）时前缀校验失败，
                // 被判 Skipped 又算"已推送"，FAIL 告警永久丢失而界面仍显示"已配置"。
                if (root.TryGetProperty("webhook_url", out v)) cfg.WebhookUrl = (GetStringSafe(v) ?? "").Trim();
                if (root.TryGetProperty("skip_historical_scan", out v)) cfg.SkipHistoricalScan = v.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("log_level", out v)) cfg.LogLevel = GetStringSafe(v) ?? "INFO";
                if (root.TryGetProperty("desktop_notify", out v)) cfg.DesktopNotify = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("notify_min_interval_sec", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var sec) && sec >= 0)
                    cfg.NotifyMinIntervalSec = sec;
                if (root.TryGetProperty("todo_scan_days", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var days) && days >= 1)
                    cfg.TodoScanDays = days;
                if (root.TryGetProperty("feishu_banner_img_key", out v)) cfg.FeishuBannerImgKey = (GetStringSafe(v) ?? "").Trim();
                if (root.TryGetProperty("feishu_banner_img_key_red", out v)) cfg.FeishuBannerImgKeyRed = (GetStringSafe(v) ?? "").Trim();
                if (root.TryGetProperty("feishu_banner_img_key_orange", out v)) cfg.FeishuBannerImgKeyOrange = (GetStringSafe(v) ?? "").Trim();
                if (root.TryGetProperty("feishu_banner_img_key_blue", out v)) cfg.FeishuBannerImgKeyBlue = (GetStringSafe(v) ?? "").Trim();
                if (root.TryGetProperty("feishu_banner_img_key_green", out v)) cfg.FeishuBannerImgKeyGreen = (GetStringSafe(v) ?? "").Trim();
                if (root.TryGetProperty("feishu_banner_img_key_yellow", out v)) cfg.FeishuBannerImgKeyYellow = (GetStringSafe(v) ?? "").Trim();
                if (root.TryGetProperty("feishu_fail_merge_enabled", out v))
                    cfg.FeishuFailMergeEnabled = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("feishu_fail_merge_window_sec", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var fmw) && fmw >= 10 && fmw <= 3600)
                    cfg.FeishuFailMergeWindowSec = fmw;
                if (root.TryGetProperty("feishu_fail_merge_max", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var fmm) && fmm >= 1 && fmm <= 200)
                    cfg.FeishuFailMergeMax = fmm;
                if (root.TryGetProperty("feishu_collect_alert_enabled", out v))
                    cfg.FeishuCollectAlertEnabled = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("feishu_collect_alert_window_min", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var caw) && caw >= 5 && caw <= 1440)
                    cfg.FeishuCollectAlertWindowMin = caw;
                if (root.TryGetProperty("feishu_collect_alert_parse_min", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var cap) && cap >= 0)
                    cfg.FeishuCollectAlertParseMin = cap;
                if (root.TryGetProperty("feishu_collect_alert_retry_min", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var car) && car >= 0)
                    cfg.FeishuCollectAlertRetryMin = car;
                if (root.TryGetProperty("feishu_collect_alert_throttle_min", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var cat) && cat >= 0)
                    cfg.FeishuCollectAlertThrottleMin = cat;
                if (root.TryGetProperty("feishu_daily_summary_enabled", out v))
                    cfg.FeishuDailySummaryEnabled = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("feishu_group_alert_enabled", out v))
                    cfg.FeishuGroupAlertEnabled = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("feishu_deviation_alert_enabled", out v))
                    cfg.FeishuDeviationAlertEnabled = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("feishu_deviation_min_score", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var dms) && dms >= 0 && dms <= 100)
                    cfg.FeishuDeviationMinScore = dms;
                if (root.TryGetProperty("feishu_deviation_max_per_day", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var dmd) && dmd >= 1)
                    cfg.FeishuDeviationMaxPerDay = dmd;
                if (root.TryGetProperty("feishu_storage_alert_enabled", out v))
                    cfg.FeishuStorageAlertEnabled = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("feishu_storage_alert_score", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var sas) && sas >= 0 && sas <= 100)
                    cfg.FeishuStorageAlertScore = sas;
                if (root.TryGetProperty("feishu_todo_overdue_enabled", out v))
                    cfg.FeishuTodoOverdueEnabled = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("feishu_todo_overdue_days", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var tod) && tod >= 1)
                    cfg.FeishuTodoOverdueDays = tod;
                if (root.TryGetProperty("feishu_todo_overdue_min_severity", out v) && v.ValueKind == JsonValueKind.String)
                {
                    var sev = (GetStringSafe(v) ?? "critical").Trim().ToLowerInvariant();
                    if (sev is "critical" or "major" or "minor") cfg.FeishuTodoOverdueMinSeverity = sev;
                }
                if (root.TryGetProperty("parsers_path", out v) && v.ValueKind == JsonValueKind.String)
                    cfg.ParsersPath = GetStringSafe(v) ?? "parsers.json";
                if (root.TryGetProperty("todo_spec_merge", out v) &&
                    (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                    cfg.TodoSpecMerge = v.GetBoolean();
                if (root.TryGetProperty("db_maintenance_hour", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var mh) && mh >= 0 && mh <= 23)
                    cfg.DbMaintenanceHour = mh;
                if (root.TryGetProperty("db_storage_auto_optimize", out v))
                    cfg.DbStorageAutoOptimize = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("db_storage_monitor_hours", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var smh) && smh >= 1 && smh <= 24)
                    cfg.DbStorageMonitorHours = smh;
                if (root.TryGetProperty("db_vacuum_threshold_mb", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var vtm) && vtm >= 100)
                    cfg.DbVacuumThresholdMb = vtm;
                if (root.TryGetProperty("db_parse_failure_retention_days", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var pfr) && pfr >= 7)
                    cfg.DbParseFailureRetentionDays = pfr;
                if (root.TryGetProperty("fct_program_source_root", out v) && v.ValueKind == JsonValueKind.String)
                    cfg.FctProgramSourceRoot = (GetStringSafe(v) ?? cfg.FctProgramSourceRoot).Trim();
                if (root.TryGetProperty("fct_program_backup_dir", out v) && v.ValueKind == JsonValueKind.String)
                    cfg.FctProgramBackupDir = (GetStringSafe(v) ?? cfg.FctProgramBackupDir).Trim();
                if (root.TryGetProperty("fct_program_backup_enabled", out v))
                    cfg.FctProgramBackupEnabled = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("fct_program_backup_keep", out v) &&
                    v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var fpk) && fpk >= 1)
                    cfg.FctProgramBackupKeep = fpk;
                if (root.TryGetProperty("auto_update", out v)) cfg.AutoUpdate = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("update_source", out v)) cfg.UpdateSource = (GetStringSafe(v) ?? "").Trim();
                if (root.TryGetProperty("update_dir", out v) && v.ValueKind == JsonValueKind.String)
                    cfg.UpdateDir = (GetStringSafe(v) ?? cfg.UpdateDir).Trim();
                if (root.TryGetProperty("learn_baseline_enabled", out v)) cfg.LearnBaselineEnabled = v.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("learn_resource_sampling_enabled", out v)) cfg.LearnResourceSamplingEnabled = v.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("learn_priority_enabled", out v)) cfg.LearnPriorityEnabled = v.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("learn_fail_merge_enabled", out v)) cfg.LearnFailMergeEnabled = v.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("learn_fail_merge_level", out v) && v.ValueKind == JsonValueKind.String)
                {
                    var lvl = (GetStringSafe(v) ?? "signal").Trim().ToLowerInvariant();
                    if (lvl == "off" || lvl == "signal" || lvl == "section") cfg.LearnFailMergeLevel = lvl;
                }
                if (root.TryGetProperty("learn_baseline_window_days", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var lbw) && lbw >= 1) cfg.LearnBaselineWindowDays = lbw;
                if (root.TryGetProperty("learn_baseline_sigma", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var lbs) && lbs > 0) cfg.LearnBaselineSigma = lbs;
                if (root.TryGetProperty("learn_baseline_min_samples", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var lbm) && lbm >= 1) cfg.LearnBaselineMinSamples = lbm;
                if (root.TryGetProperty("learn_normal_enabled", out v)) cfg.LearnNormalEnabled = v.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("learn_normal_min_samples", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var lnm) && lnm >= 1) cfg.LearnNormalMinSamples = lnm;
                if (root.TryGetProperty("learn_normal_stale_days", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var lns) && lns >= 1) cfg.LearnNormalStaleDays = lns;
                if (root.TryGetProperty("learn_normal_vote_signals", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var lnv) && lnv >= 1) cfg.LearnNormalVoteSignals = lnv;
                if (root.TryGetProperty("learn_normal_event_score", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var lne) && lne >= 1) cfg.LearnNormalEventScore = Math.Min(100, lne);
                if (root.TryGetProperty("learn_normal_max_events_per_day", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var lnx) && lnx >= 1) cfg.LearnNormalMaxEventsPerDay = lnx;
                if (root.TryGetProperty("learn_group_alert_min", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var lga) && lga >= 1) cfg.LearnGroupAlertMin = lga;
                if (root.TryGetProperty("learn_resource_retention_days", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var lrr) && lrr >= 1) cfg.LearnResourceRetentionDays = lrr;
                if (root.TryGetProperty("analyze_collect_pass", out v)) cfg.AnalyzeCollectPass = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("analyze_max_file_kb", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var amf) && amf >= 1) cfg.AnalyzeMaxFileKb = amf;
                if (root.TryGetProperty("analyze_max_tests_per_file", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var amt) && amt >= 1) cfg.AnalyzeMaxTestsPerFile = amt;
                if (root.TryGetProperty("analyze_drift_enabled", out v)) cfg.AnalyzeDriftEnabled = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("analyze_cpk_enabled", out v)) cfg.AnalyzeCpkEnabled = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("analyze_window_days", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var awd) && awd >= 1) cfg.AnalyzeWindowDays = awd;
                if (root.TryGetProperty("analyze_min_samples", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var ams) && ams >= 2) cfg.AnalyzeMinSamples = ams; // 审计 M6：下限收紧为 2（单样本 σ 无意义）
                if (root.TryGetProperty("analyze_drift_sigma", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var ads) && ads > 0) cfg.AnalyzeDriftSigma = ads;
                if (root.TryGetProperty("analyze_drift_warn_ratio", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var awr) && awr > 0) cfg.AnalyzeDriftWarnRatio = awr;
                if (root.TryGetProperty("analyze_measure_retention_days", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var amr) && amr >= 1) cfg.AnalyzeMeasureRetentionDays = amr;
                if (root.TryGetProperty("analyze_failitem_retention_days", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var afi) && afi >= 1) cfg.AnalyzeFailItemRetentionDays = afi;
                if (root.TryGetProperty("analyze_tdms_enabled", out v)) cfg.AnalyzeTdmsEnabled = v.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("tdms_root", out v)) cfg.TdmsRoot = (GetStringSafe(v) ?? "").Trim();
                if (root.TryGetProperty("analyze_tdms_max_kb", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var atk) && atk >= 1) cfg.AnalyzeTdmsMaxKb = atk;
                if (root.TryGetProperty("analyze_tdms_max_channels", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var atc) && atc >= 1) cfg.AnalyzeTdmsMaxChannels = atc;
                if (root.TryGetProperty("analyze_fail_attr_enabled", out v)) cfg.AnalyzeFailAttrEnabled = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("analyze_fixture_enabled", out v)) cfg.AnalyzeFixtureEnabled = v.ValueKind != JsonValueKind.False;
                if (root.TryGetProperty("analyze_attr_min_bucket", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var aam) && aam >= 1) cfg.AnalyzeAttrMinBucket = aam;
                if (root.TryGetProperty("analyze_attr_dev_ratio", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var aad) && aad > 0) cfg.AnalyzeAttrDevRatio = aad;
                if (root.TryGetProperty("analyze_effect_d", out v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var aed) && aed > 0) cfg.AnalyzeEffectD = aed;
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
            // 审计 D2：config.json 损坏时尝试从最近备份恢复一次，避免现场配置静默丢失且无回退
            if (allowBackupRestore)
            {
                try
                {
                    var latest = ListBackups(1).FirstOrDefault();
                    if (latest != null && File.Exists(latest))
                    {
                        File.Copy(latest, path, overwrite: true);
                        Logger.Warning($"[配置] 已从最近备份恢复: {Path.GetFileName(latest)}，重新加载");
                        return Load(false);
                    }
                }
                catch (Exception rex) { Logger.Error($"[配置] 备份恢复失败: {rex.Message}"); }
            }
        }
        return new AppConfig();
    }

    /// <summary>把 loaded 的字段拷到 this，保持对象引用不变（Engine/MainForm 持 Instance）。</summary>
    public void CopyFrom(AppConfig src)
    {
        if (src == null || ReferenceEquals(src, this)) return;
        foreach (var p in typeof(AppConfig).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite) continue;
            p.SetValue(this, p.GetValue(src));
        }
    }

    /// <summary>热加载：读盘后原地写入 Instance，不 new 掉 Engine 手里的引用。</summary>
    public static bool ReloadFromDisk()
    {
        try
        {
            // 审计：Load() 内部把所有异常吞掉后 return new AppConfig()（全默认值），
            // ReloadFromDisk 无法区分"读失败"和"读成功"——任何一次瞬时读取失败
            // （文件被占用 / 磁盘抖动 / 读到半份写入）都会把运行中的 station_id / results_root /
            // webhook_url / 全部开关静默复位。这里先做一次显式预校验，不通过就保持旧值。
            var path = Path.Combine(BaseDir, "config.json");
            if (!File.Exists(path))
            {
                Logger.Warning("[配置] 热加载跳过：config.json 不存在（保持当前值）");
                return false;
            }
            try
            {
                using var probe = JsonDocument.Parse(File.ReadAllText(path));
                if (probe.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException("根节点不是 JSON 对象");
            }
            catch (Exception vex)
            {
                Logger.Warning($"[配置] 热加载跳过：config.json 不可读或非法 JSON（保持当前值）: {vex.Message}");
                return false;
            }

            var loaded = Load(false);
            Instance.CopyFrom(loaded);
            try { Logger.SetLevel(Instance.LogLevel); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[配置] 热加载失败，保持旧值: {ex.Message}");
            return false;
        }
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
                var bak = Path.Combine(bakDir, $"config_backup_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}.json");
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
                    "feishu_banner_img_key",
                    "feishu_banner_img_key_red", "feishu_banner_img_key_orange", "feishu_banner_img_key_blue",
                    "feishu_banner_img_key_green", "feishu_banner_img_key_yellow",
                    "parsers_path",
                    "feishu_fail_merge_enabled", "feishu_fail_merge_window_sec", "feishu_fail_merge_max",
                    "feishu_collect_alert_enabled", "feishu_collect_alert_window_min",
                    "feishu_collect_alert_parse_min", "feishu_collect_alert_retry_min",
                    "feishu_collect_alert_throttle_min", "feishu_daily_summary_enabled",
                    "feishu_group_alert_enabled", "feishu_deviation_alert_enabled",
                    "feishu_deviation_min_score", "feishu_deviation_max_per_day",
                    "feishu_storage_alert_enabled", "feishu_storage_alert_score",
                    "feishu_todo_overdue_enabled", "feishu_todo_overdue_days", "feishu_todo_overdue_min_severity",
                    "todo_spec_merge",
                    "db_maintenance_hour", "db_storage_auto_optimize", "db_storage_monitor_hours",
                    "db_vacuum_threshold_mb", "db_parse_failure_retention_days",
                    "fct_program_source_root", "fct_program_backup_dir", "fct_program_backup_enabled",
                    "fct_program_backup_keep",
                    "auto_update", "update_source", "update_dir",
                    "learn_baseline_enabled", "learn_resource_sampling_enabled", "learn_priority_enabled",
                    "learn_fail_merge_enabled", "learn_fail_merge_level", "learn_baseline_window_days",
                    "learn_baseline_sigma", "learn_baseline_min_samples", "learn_normal_enabled",
                    "learn_normal_min_samples", "learn_normal_stale_days",
                    "learn_normal_vote_signals", "learn_normal_event_score", "learn_normal_max_events_per_day",
                    "learn_group_alert_min",
                    "learn_resource_retention_days",
                    "analyze_collect_pass", "analyze_max_file_kb", "analyze_max_tests_per_file",
                    "analyze_drift_enabled", "analyze_cpk_enabled", "analyze_window_days",
                    "analyze_min_samples", "analyze_drift_sigma", "analyze_drift_warn_ratio",
                    "analyze_measure_retention_days", "analyze_failitem_retention_days", "analyze_tdms_enabled",
                    "tdms_root", "analyze_tdms_max_kb", "analyze_tdms_max_channels",
                    "analyze_fail_attr_enabled", "analyze_fixture_enabled", "analyze_attr_min_bucket",
                    "analyze_attr_dev_ratio", "analyze_effect_d",
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
                writer.WriteString("feishu_banner_img_key", FeishuBannerImgKey);
                writer.WriteString("feishu_banner_img_key_red", FeishuBannerImgKeyRed);
                writer.WriteString("feishu_banner_img_key_orange", FeishuBannerImgKeyOrange);
                writer.WriteString("feishu_banner_img_key_blue", FeishuBannerImgKeyBlue);
                writer.WriteString("feishu_banner_img_key_green", FeishuBannerImgKeyGreen);
                writer.WriteString("feishu_banner_img_key_yellow", FeishuBannerImgKeyYellow);
                writer.WriteBoolean("feishu_fail_merge_enabled", FeishuFailMergeEnabled);
                writer.WriteNumber("feishu_fail_merge_window_sec", FeishuFailMergeWindowSec);
                writer.WriteNumber("feishu_fail_merge_max", FeishuFailMergeMax);
                writer.WriteBoolean("feishu_collect_alert_enabled", FeishuCollectAlertEnabled);
                writer.WriteNumber("feishu_collect_alert_window_min", FeishuCollectAlertWindowMin);
                writer.WriteNumber("feishu_collect_alert_parse_min", FeishuCollectAlertParseMin);
                writer.WriteNumber("feishu_collect_alert_retry_min", FeishuCollectAlertRetryMin);
                writer.WriteNumber("feishu_collect_alert_throttle_min", FeishuCollectAlertThrottleMin);
                writer.WriteBoolean("feishu_daily_summary_enabled", FeishuDailySummaryEnabled);
                writer.WriteBoolean("feishu_group_alert_enabled", FeishuGroupAlertEnabled);
                writer.WriteBoolean("feishu_deviation_alert_enabled", FeishuDeviationAlertEnabled);
                writer.WriteNumber("feishu_deviation_min_score", FeishuDeviationMinScore);
                writer.WriteNumber("feishu_deviation_max_per_day", FeishuDeviationMaxPerDay);
                writer.WriteBoolean("feishu_storage_alert_enabled", FeishuStorageAlertEnabled);
                writer.WriteNumber("feishu_storage_alert_score", FeishuStorageAlertScore);
                writer.WriteBoolean("feishu_todo_overdue_enabled", FeishuTodoOverdueEnabled);
                writer.WriteNumber("feishu_todo_overdue_days", FeishuTodoOverdueDays);
                writer.WriteString("feishu_todo_overdue_min_severity", FeishuTodoOverdueMinSeverity);
                writer.WriteString("parsers_path", ParsersPath);
                writer.WriteBoolean("todo_spec_merge", TodoSpecMerge);
                writer.WriteNumber("db_maintenance_hour", DbMaintenanceHour);
                writer.WriteBoolean("db_storage_auto_optimize", DbStorageAutoOptimize);
                writer.WriteNumber("db_storage_monitor_hours", DbStorageMonitorHours);
                writer.WriteNumber("db_vacuum_threshold_mb", DbVacuumThresholdMb);
                writer.WriteNumber("db_parse_failure_retention_days", DbParseFailureRetentionDays);
                writer.WriteString("fct_program_source_root", FctProgramSourceRoot);
                writer.WriteString("fct_program_backup_dir", FctProgramBackupDir);
                writer.WriteBoolean("fct_program_backup_enabled", FctProgramBackupEnabled);
                writer.WriteNumber("fct_program_backup_keep", FctProgramBackupKeep);
                writer.WriteBoolean("auto_update", AutoUpdate);
                writer.WriteString("update_source", UpdateSource);
                writer.WriteString("update_dir", UpdateDir);
                writer.WriteBoolean("learn_baseline_enabled", LearnBaselineEnabled);
                writer.WriteBoolean("learn_resource_sampling_enabled", LearnResourceSamplingEnabled);
                writer.WriteBoolean("learn_priority_enabled", LearnPriorityEnabled);
                writer.WriteBoolean("learn_fail_merge_enabled", LearnFailMergeEnabled);
                writer.WriteString("learn_fail_merge_level", LearnFailMergeLevel);
                writer.WriteNumber("learn_baseline_window_days", LearnBaselineWindowDays);
                writer.WriteNumber("learn_baseline_sigma", LearnBaselineSigma);
                writer.WriteNumber("learn_baseline_min_samples", LearnBaselineMinSamples);
                writer.WriteBoolean("learn_normal_enabled", LearnNormalEnabled);
                writer.WriteNumber("learn_normal_min_samples", LearnNormalMinSamples);
                writer.WriteNumber("learn_normal_stale_days", LearnNormalStaleDays);
                writer.WriteNumber("learn_normal_vote_signals", LearnNormalVoteSignals);
                writer.WriteNumber("learn_normal_event_score", LearnNormalEventScore);
                writer.WriteNumber("learn_normal_max_events_per_day", LearnNormalMaxEventsPerDay);
                writer.WriteNumber("learn_group_alert_min", LearnGroupAlertMin);
                writer.WriteNumber("learn_resource_retention_days", LearnResourceRetentionDays);
                writer.WriteBoolean("analyze_collect_pass", AnalyzeCollectPass);
                writer.WriteNumber("analyze_max_file_kb", AnalyzeMaxFileKb);
                writer.WriteNumber("analyze_max_tests_per_file", AnalyzeMaxTestsPerFile);
                writer.WriteBoolean("analyze_drift_enabled", AnalyzeDriftEnabled);
                writer.WriteBoolean("analyze_cpk_enabled", AnalyzeCpkEnabled);
                writer.WriteNumber("analyze_window_days", AnalyzeWindowDays);
                writer.WriteNumber("analyze_min_samples", AnalyzeMinSamples);
                writer.WriteNumber("analyze_drift_sigma", AnalyzeDriftSigma);
                writer.WriteNumber("analyze_drift_warn_ratio", AnalyzeDriftWarnRatio);
                writer.WriteNumber("analyze_measure_retention_days", AnalyzeMeasureRetentionDays);
                writer.WriteNumber("analyze_failitem_retention_days", AnalyzeFailItemRetentionDays);
                writer.WriteBoolean("analyze_tdms_enabled", AnalyzeTdmsEnabled);
                writer.WriteString("tdms_root", TdmsRoot);
                writer.WriteNumber("analyze_tdms_max_kb", AnalyzeTdmsMaxKb);
                writer.WriteNumber("analyze_tdms_max_channels", AnalyzeTdmsMaxChannels);
                writer.WriteBoolean("analyze_fail_attr_enabled", AnalyzeFailAttrEnabled);
                writer.WriteBoolean("analyze_fixture_enabled", AnalyzeFixtureEnabled);
                writer.WriteNumber("analyze_attr_min_bucket", AnalyzeAttrMinBucket);
                writer.WriteNumber("analyze_attr_dev_ratio", AnalyzeAttrDevRatio);
                writer.WriteNumber("analyze_effect_d", AnalyzeEffectD);
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
            else
            {
                // 安全加固（审计 C3）：回滚源只允许来自 config_backups 内——一律按文件名重建路径，
                // 拦截绝对路径与 ..\ 穿越属性，防止把服务器上任意可读文件覆写为 config.json
                backupPath = Path.Combine(dir, Path.GetFileName(backupPath));
            }
            if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath)) return false;
            var dest = Path.Combine(BaseDir, "config.json");
            File.Copy(backupPath, dest, overwrite:true);
            _instance = null;
            Logger.Info($"[配置] 已回滚到 {Path.GetFileName(backupPath)}");
            return true;
        } catch (Exception ex) { Logger.Error($"[配置] 回滚失败: {ex.Message}"); return false; }
    }
}
