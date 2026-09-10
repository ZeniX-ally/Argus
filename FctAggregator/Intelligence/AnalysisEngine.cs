using System.Text.Json;

namespace FctAggregator;

/// <summary>测量值分组统计（聚合查询产物，供漂移/CPK 纯函数消费）。</summary>
public sealed class MeasureStatsRow
{
    public string Model = "";
    public string TestName = "";
    public int N;
    public double Mean;
    public double Sigma;
    public double? Lolim;
    public double? Hilim;
    public string? Unit;
}

public sealed class DriftAlertItem
{
    public string Model { get; set; } = "";
    public string TestName { get; set; } = "";
    public string Kind { get; set; } = "";
    public double TodayMean { get; set; }
    public double BaseMean { get; set; }
    public double SigmaEff { get; set; }
    public double Score { get; set; }
    public double ExpectedLow { get; set; }
    public double ExpectedHigh { get; set; }
    public double? Lolim { get; set; }
    public double? Hilim { get; set; }
    public string? Unit { get; set; }
    public int NBase { get; set; }
    public int NToday { get; set; }
    public string Message { get; set; } = "";
}

public sealed class DriftState
{
    public string Date { get; set; } = "";
    public int WindowDays { get; set; }
    public int AnalyzedTests { get; set; }
    public List<DriftAlertItem> Alerts { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this);
    public static DriftState? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<DriftState>(json); }
        catch { return null; }
    }
}

public sealed class CpkItem
{
    public string Model { get; set; } = "";
    public string TestName { get; set; } = "";
    public double? Cpk { get; set; }
    public string Level { get; set; } = "";
    public int N { get; set; }
    public string? Unit { get; set; }
    public double? Lolim { get; set; }
    public double? Hilim { get; set; }
}

public sealed class CpkState
{
    public string Date { get; set; } = "";
    public int WindowDays { get; set; }
    public int AnalyzedTests { get; set; }
    public int LowCount { get; set; }
    public List<CpkItem> Items { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this);
    public static CpkState? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<CpkState>(json); }
        catch { return null; }
    }
}

public sealed class TdmsState
{
    public string Date { get; set; } = "";
    public long RowsTotal { get; set; }
    public long RecordsTotal { get; set; }
    public string LastTs { get; set; } = "";
    public int FilesOk { get; set; }
    public int FilesSkipped { get; set; }
    public int Dropped { get; set; }
    public string LastError { get; set; } = "";
    public string LastErrorPath { get; set; } = "";

    public string ToJson() => JsonSerializer.Serialize(this);
    public static TdmsState? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<TdmsState>(json); }
        catch { return null; }
    }
}

/// <summary>规格06 多元深分析引擎：漂移预警 + CPK 过程能力（照 LearningEngine 模式，挂 DbMaintenance 链尾）。</summary>
public static class AnalysisEngine
{
    public const string MetaDrift = "analyze_drift_state";
    public const string MetaCpk = "analyze_cpk_state";
    public const string MetaAttribution = "analyze_attribution_state";
    public const string MetaFixture = "analyze_fixture_state";
    public const string MetaTdms = "analyze_tdms_state";
    public const int MaxAlerts = 100;
    public const int MaxCpkItems = 200;
    public const double CpkLowThreshold = 1.33;

    public static void RunOnce(Database db, AppConfig cfg, DateTime? now = null)
    {
        if (!cfg.AnalyzeDriftEnabled && !cfg.AnalyzeCpkEnabled
            && !cfg.AnalyzeFailAttrEnabled && !cfg.AnalyzeFixtureEnabled
            && !cfg.AnalyzeTdmsEnabled) return;
        var nowTs = now ?? DateTime.Now;
        var today = nowTs.Date;
        if (cfg.AnalyzeDriftEnabled)
        {
            try { RunDrift(db, cfg, today, nowTs); }
            catch (Exception ex) { Logger.Warning($"[分析] 漂移预警失败: {ex.Message}"); }
        }
        if (cfg.AnalyzeCpkEnabled)
        {
            try { RunCpk(db, cfg, today); }
            catch (Exception ex) { Logger.Warning($"[分析] CPK 计算失败: {ex.Message}"); }
        }
        if (cfg.AnalyzeFailAttrEnabled)
        {
            try
            {
                var state = FailAttributor.Run(db, cfg, today);
                db.SetMeta(MetaAttribution, state.ToJson());
            }
            catch (Exception ex) { Logger.Warning($"[分析] 失败归因失败: {ex.Message}"); }
        }
        if (cfg.AnalyzeFixtureEnabled)
        {
            try
            {
                var state = FixtureComparator.Run(db, cfg, today);
                db.SetMeta(MetaFixture, state.ToJson());
            }
            catch (Exception ex) { Logger.Warning($"[分析] tester 对比失败: {ex.Message}"); }
        }
        if (cfg.AnalyzeTdmsEnabled)
        {
            try
            {
                var sum = db.GetTdmsFeatureSummary();
                var state = new TdmsState
                {
                    Date = today.ToString("yyyy-MM-dd"),
                    RowsTotal = sum.Rows,
                    RecordsTotal = sum.Records,
                    LastTs = sum.LastTs,
                    FilesOk = TdmsFeatureCollector.FilesOk,
                    FilesSkipped = TdmsFeatureCollector.FilesSkipped,
                    Dropped = TdmsFeatureCollector.Dropped,
                    LastError = TdmsFeatureCollector.LastError,
                    LastErrorPath = TdmsFeatureCollector.LastErrorPath,
                };
                db.SetMeta(MetaTdms, state.ToJson());
            }
            catch (Exception ex) { Logger.Warning($"[分析] TDMS 快照失败: {ex.Message}"); }
        }
    }

    private static void RunDrift(Database db, AppConfig cfg, DateTime today, DateTime nowTs)
    {
        var w = Math.Max(1, cfg.AnalyzeWindowDays);
        var todayStr = today.ToString("yyyy-MM-dd");
        var baseStats = db.AggregateMeasurementStats(today.AddDays(-w).ToString("yyyy-MM-dd"), todayStr);
        // 审计 C-I3："今日"窗口原为 today 00:00~24:00——维护链默认凌晨 3 点执行时实为 00:00~03:00 部分天，
        // 凌晨几个样本即可触发假漂移。改为严格"今日 00:00 ~ now"：基线是 [today-w, today)、今日是 [today, now]，
        // 这才是真正零重叠。
        // 注意：曾用过"过去 24h"（now-24h ~ now），但那与基线窗口重叠约 21 小时（now 取凌晨 3 点时尤为明显），
        // 昨天那批样本会同时进基线与今日，把今日均值拉向基线、系统性削弱漂移检出。
        var todayStats = db.AggregateMeasurementStats(
            todayStr + " 00:00:00",
            nowTs.AddSeconds(1).ToString("yyyy-MM-dd HH:mm:ss"));
        var alerts = DriftAnalyzer.Compute(baseStats, todayStats, cfg);
        var state = new DriftState
        {
            Date = todayStr,
            WindowDays = w,
            AnalyzedTests = todayStats.Count,
            Alerts = alerts.Take(MaxAlerts).ToList(),
        };
        db.SetMeta(MetaDrift, state.ToJson());
        if (state.Alerts.Count > 0)
            Logger.Warning($"[分析] 漂移/逼近限预警 {state.Alerts.Count} 项（基线窗口 {w} 天，近24h分析 {state.AnalyzedTests} 测项）");
    }

    private static void RunCpk(Database db, AppConfig cfg, DateTime today)
    {
        var w = Math.Max(1, cfg.AnalyzeWindowDays);
        var stats = db.AggregateMeasurementStats(
            today.AddDays(-(w - 1)).ToString("yyyy-MM-dd"),
            today.AddDays(1).ToString("yyyy-MM-dd"));
        var items = CpkAnalyzer.Compute(stats);
        var state = new CpkState
        {
            Date = today.ToString("yyyy-MM-dd"),
            WindowDays = w,
            AnalyzedTests = items.Count,
            LowCount = items.Count(i => i.Level == "low"),
            Items = items.Where(i => i.Level == "low").OrderBy(i => i.Cpk).Take(MaxCpkItems).ToList(),
        };
        db.SetMeta(MetaCpk, state.ToJson());
        if (state.LowCount > 0)
            Logger.Info($"[分析] CPK 低能力 {state.LowCount}/{items.Count} 项（窗口 {w} 天）");
    }

    public static DriftState? GetDriftState(Database db) => DriftState.FromJson(db.GetMeta(MetaDrift));
    public static CpkState? GetCpkState(Database db) => CpkState.FromJson(db.GetMeta(MetaCpk));
    public static AttributionState? GetAttributionState(Database db) => AttributionState.FromJson(db.GetMeta(MetaAttribution));
    public static FixtureState? GetFixtureState(Database db) => FixtureState.FromJson(db.GetMeta(MetaFixture));
    public static TdmsState? GetTdmsState(Database db) => TdmsState.FromJson(db.GetMeta(MetaTdms));
}
