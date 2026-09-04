using System.Text.Json;

namespace FctAggregator;

/// <summary>章节群挂预警条目（规格 05 S4，落 app_meta 供主页/总览只读呈现）。</summary>
public sealed class GroupAlertItem
{
    public string Section { get; set; } = "";
    public int DistinctSignalCount { get; set; }
    public List<string> Signals { get; set; } = new();
    public string Hint { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>群挂检测状态快照（app_meta: learn_group_alerts）。</summary>
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

/// <summary>
/// 规格 05：机台端自学习引擎 —— 每日调度入口。
/// S3 基线（learn_baseline_enabled）、S4 章节群挂（learn_fail_merge_enabled）、
/// S4 维修/待办权重校准（learn_priority_enabled）三段独立执行，互不拖累；
/// 全部开关关闭时为 no-op（行为 100% 兼容 v3.23.3）。
/// 状态落本机库 app_meta，可查可重置；预警只标记不强动作。
/// </summary>
public static class LearningEngine
{
    public const string MetaBaseline = "learn_baseline_state";
    public const string MetaGroupAlerts = "learn_group_alerts";
    public const string MetaPriorityFactors = "learn_priority_factors";

    /// <summary>权重校准因子缓存（RunOnce/LoadPriorityFactors 后刷新；key = TodoGrouping.KeyOf(failItem)）。</summary>
    private static volatile Dictionary<string, double>? _priorityFactors;

    /// <summary>
    /// 每日窗口调用（DbMaintenance）或测试直接调用。
    /// now 可注入（自检确定性）；逐段独立 try/catch，一段失败不影响其余。
    /// </summary>
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

    // ==================== S3：良率/产能滚动基线 ====================

    private static void RunBaseline(Database db, AppConfig cfg, DateTime? now)
    {
        var records = db.FetchBaselineSourceRecords(cfg.LearnBaselineWindowDays, now);
        var state = SelfBaseline.Compute(records, cfg, now);
        db.SetMeta(MetaBaseline, state.ToJson());
        if (state.Alerts.Count > 0)
            Logger.Info($"[自学习] 基线评估完成: {state.Buckets.Count} 桶，今日预警 {state.Alerts.Count} 条（{string.Join("；", state.Alerts.Select(a => a.Kind))}）");
    }

    /// <summary>读取最近一次基线状态（无/损坏返回 null）。设置页与主页预警条目数据源。</summary>
    public static BaselineState? GetBaselineState(Database db)
        => BaselineState.FromJson(db.GetMeta(MetaBaseline));

    // ==================== S4：章节群挂检测 + 根因指向 ====================

    private static void RunGroupAlerts(Database db, AppConfig cfg, DateTime? now)
    {
        var today = (now ?? DateTime.Now).Date;
        var reasons = db.FetchDayFailReasons(today.ToString("yyyy-MM-dd"));
        // 一条 fail_reason 可能含多项失败（换行/分号分隔），与看板 Top 排行同口径拆分
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

    /// <summary>读取最近一次群挂状态（无/损坏返回 null）。</summary>
    public static GroupAlertState? GetGroupAlerts(Database db)
        => GroupAlertState.FromJson(db.GetMeta(MetaGroupAlerts));

    // ==================== S4：维修/待办权重校准 ====================

    /// <summary>校准上限（规格 §3.3：clamp [0.5, 2.0] 防跑飞）。</summary>
    public const double FactorMin = 0.5;
    public const double FactorMax = 2.0;

    /// <summary>
    /// 单项校准因子（纯函数）：实际被处理过的故障项上调（+0.1/次，封顶 +0.5）、
    /// 被显式删除/忽略的下调（−0.1/次，封顶 −0.5），乘性合成后 clamp [0.5, 2.0]。
    /// 仅影响排序分，不改状态机、不改数据。
    /// </summary>
    public static double CalibrateFactor(int resolvedCount, int dismissedCount)
    {
        double up = 1.0 + 0.1 * Math.Clamp(resolvedCount, 0, 5);
        double down = 1.0 - 0.1 * Math.Clamp(dismissedCount, 0, 5);
        return Math.Clamp(up * down, FactorMin, FactorMax);
    }

    private static void RunPriorityFactors(Database db)
    {
        // 已完成维修（实际被处理过）按归一化大项计数
        var resolved = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in db.ListMaintenance("", 2000))
        {
            if (!string.Equals(m.Status, "resolved", StringComparison.OrdinalIgnoreCase)) continue;
            var key = TodoGrouping.KeyOf(m.FailItem);
            if (key.Length == 0) continue;
            resolved[key] = resolved.GetValueOrDefault(key) + 1;
        }
        // 显式删除的待办（dismissed_todos，v3.3.0 起 DeleteTodo 落库）计数
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

    /// <summary>从 app_meta 重新载入校准因子缓存（启动/重置后调用）。</summary>
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

    /// <summary>取某故障项的校准因子（未学习/未命中 = 1.0 原权重）。</summary>
    public static double FactorOf(string? failItem)
    {
        if (string.IsNullOrWhiteSpace(failItem) || _priorityFactors == null) return 1.0;
        var key = TodoGrouping.KeyOf(failItem);
        return _priorityFactors.TryGetValue(key, out var f) ? f : 1.0;
    }

    /// <summary>清空校准（设置页「一键重置」）：删 meta + 清缓存。</summary>
    public static void ResetPriorityFactors(Database db)
    {
        db.SetMeta(MetaPriorityFactors, "{}");
        _priorityFactors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    }
}
