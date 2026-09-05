using System.Text.Json;

namespace FctAggregator;

public sealed class GroupAlertItem
{
    public string Section { get; set; } = "";
    public int DistinctSignalCount { get; set; }
    public List<string> Signals { get; set; } = new();
    public string Hint { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class GroupAlertState
{
    public string Date { get; set; } = "";
    public int Threshold { get; set; }
    public List<GroupAlertItem> Alerts { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this);

    public static GroupAlertState? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<GroupAlertState>(json); }
        catch { return null; }
    }
}

public static class LearningEngine
{
    public const string MetaBaseline = "learn_baseline_state";
    public const string MetaGroupAlerts = "learn_group_alerts";
    public const string MetaPriorityFactors = "learn_priority_factors";

    private static volatile Dictionary<string, double>? _priorityFactors;

    public static void RunOnce(Database db, AppConfig cfg, DateTime? now = null)
    {
        if (!cfg.LearnBaselineEnabled && !cfg.LearnFailMergeEnabled && !cfg.LearnPriorityEnabled) return;

        if (cfg.LearnBaselineEnabled)
        {
            try { RunBaseline(db, cfg, now); }
            catch (Exception ex) { Logger.Warning($"[自学习] 基线计算失败: {ex.Message}"); }
        }
        if (cfg.LearnFailMergeEnabled)
        {
            try { RunGroupAlerts(db, cfg, now); }
            catch (Exception ex) { Logger.Warning($"[自学习] 章节群挂检测失败: {ex.Message}"); }
        }
        if (cfg.LearnPriorityEnabled)
        {
            try { RunPriorityFactors(db); }
            catch (Exception ex) { Logger.Warning($"[自学习] 权重校准失败: {ex.Message}"); }
        }
    }

    private static void RunBaseline(Database db, AppConfig cfg, DateTime? now)
    {
        var records = db.FetchBaselineSourceRecords(cfg.LearnBaselineWindowDays, now);
        var state = SelfBaseline.Compute(records, cfg, now);
        db.SetMeta(MetaBaseline, state.ToJson());
        if (state.Alerts.Count > 0)
            Logger.Info($"[自学习] 基线评估完成: {state.Buckets.Count} 桶，今日预警 {state.Alerts.Count} 条（{string.Join("；", state.Alerts.Select(a => a.Kind))}）");
    }

    public static BaselineState? GetBaselineState(Database db)
        => BaselineState.FromJson(db.GetMeta(MetaBaseline));

    private static void RunGroupAlerts(Database db, AppConfig cfg, DateTime? now)
    {
        var today = (now ?? DateTime.Now).Date;
        var reasons = db.FetchDayFailReasons(today.ToString("yyyy-MM-dd"));
        var items = reasons.SelectMany(s => s.Split(new[] { '\r', '\n', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
                           .Select(s => s.Trim())
                           .Where(s => s.Length > 0)
                           .ToList();
        var results = FailReasonMerger.CheckSectionGroupAlert(items, cfg.LearnGroupAlertMin);
        var state = new GroupAlertState
        {
            Date = today.ToString("yyyy-MM-dd"),
            Threshold = cfg.LearnGroupAlertMin,
            Alerts = results.Select(a => new GroupAlertItem
            {
                Section = a.Section,
                DistinctSignalCount = a.DistinctSignalCount,
                Signals = a.SignalNames,
                Hint = a.RootCauseHint,
                Message = a.AlertMessage,
            }).ToList(),
        };
        db.SetMeta(MetaGroupAlerts, state.ToJson());
        if (state.Alerts.Count > 0)
            Logger.Warning($"[自学习] 章节群挂: {string.Join("；", state.Alerts.Select(a => $"§{a.Section} {a.DistinctSignalCount} 信号"))}");
    }

    public static GroupAlertState? GetGroupAlerts(Database db)
        => GroupAlertState.FromJson(db.GetMeta(MetaGroupAlerts));

    public const double FactorMin = 0.5;
    public const double FactorMax = 2.0;

    public static double CalibrateFactor(int resolvedCount, int dismissedCount)
    {
        double up = 1.0 + 0.1 * Math.Clamp(resolvedCount, 0, 5);
        double down = 1.0 - 0.1 * Math.Clamp(dismissedCount, 0, 5);
        return Math.Clamp(up * down, FactorMin, FactorMax);
    }

    private static void RunPriorityFactors(Database db)
    {
        var resolved = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in db.ListMaintenance("", 2000))
        {
            if (!string.Equals(m.Status, "resolved", StringComparison.OrdinalIgnoreCase)) continue;
            var key = TodoGrouping.KeyOf(m.FailItem);
            if (key.Length == 0) continue;
            resolved[key] = resolved.GetValueOrDefault(key) + 1;
        }
        var dismissed = db.CountDismissedByItem()
            .GroupBy(kv => TodoGrouping.KeyOf(kv.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value), StringComparer.OrdinalIgnoreCase);

        var factors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in resolved.Keys.Union(dismissed.Keys, StringComparer.OrdinalIgnoreCase))
        {
            factors[k] = Math.Round(CalibrateFactor(resolved.GetValueOrDefault(k), dismissed.GetValueOrDefault(k)), 3);
        }
        db.SetMeta(MetaPriorityFactors, JsonSerializer.Serialize(factors));
        _priorityFactors = new Dictionary<string, double>(factors, StringComparer.OrdinalIgnoreCase);
    }

    public static void LoadPriorityFactors(Database db)
    {
        try
        {
            var json = db.GetMeta(MetaPriorityFactors);
            _priorityFactors = string.IsNullOrWhiteSpace(json)
                ? null
                : new Dictionary<string, double>(
                    JsonSerializer.Deserialize<Dictionary<string, double>>(json) ?? new(),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch { _priorityFactors = null; }
    }

    public static double FactorOf(string? failItem)
    {
        if (string.IsNullOrWhiteSpace(failItem) || _priorityFactors == null) return 1.0;
        var key = TodoGrouping.KeyOf(failItem);
        return _priorityFactors.TryGetValue(key, out var f) ? f : 1.0;
    }

    public static void ResetPriorityFactors(Database db)
    {
        db.SetMeta(MetaPriorityFactors, "{}");
        _priorityFactors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    }
}
