namespace FctAggregator;

/// <summary>存储健康告警卡的载荷。</summary>
public sealed class StorageAlertPayload
{
    public string StationId { get; set; } = "";
    public int Score { get; set; }
    public int Threshold { get; set; }
    public long FileBytes { get; set; }
    public long WalBytes { get; set; }
    public double BloatRatio { get; set; }
    public List<string> Issues { get; set; } = new();
    public List<string> PlannedActions { get; set; } = new();
}

/// <summary>待办超期未闭环告警卡的载荷。</summary>
public sealed class TodoOverduePayload
{
    public string StationId { get; set; } = "";
    public int OverdueDays { get; set; }
    public string MinSeverity { get; set; } = "critical";
    public List<MaintenanceRecord> Items { get; set; } = new();
    /// <summary>超期总数（Items 已按上限截断，用它提示「另有 N 条」）。</summary>
    public int TotalOverdue { get; set; }
}

/// <summary>
/// 运维类告警（存储健康 / 待办超期未闭环）。
///
/// 两者都跟着**每日维护**跑（数据是「天」级变化，没必要更密），并按日期去重——
/// `DbMaintenance.RunNow` 在每个进程启动时都会补跑一次，不去重就会重启几次推几张。
/// </summary>
public static class OpsAlertMonitor
{
    public const string MetaStoragePush = "feishu_storage_alert_last_push";
    public const string MetaTodoPush = "feishu_todo_overdue_last_push";

    /// <summary>待办列表一次最多列出的条数（超出只给总数提示）。</summary>
    public const int TodoMaxListed = 10;

    // ───────────────────────── 通用：严重度 ─────────────────────────

    /// <summary>严重度排序权重（critical &gt; major &gt; minor），未知一律 0。</summary>
    public static int SeverityRank(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        "critical" => 3,
        "major" => 2,
        "minor" => 1,
        _ => 0,
    };

    /// <summary>纯判定：是否属于「超期未闭环」——未完成态 + 创建时间早于 N 天前 + 严重度达标。</summary>
    public static bool IsOverdue(string? status, string? createdAt, string? severity,
        DateTime now, int overdueDays, int minRank)
    {
        var norm = MaintenanceMeta.Normalize(status);
        if (norm != MaintenanceMeta.DefaultStatus && norm != "in_progress") return false;
        if (SeverityRank(severity) < minRank) return false;
        if (string.IsNullOrWhiteSpace(createdAt)) return false;
        if (!DateTime.TryParse(createdAt, out var created)) return false;
        return (now - created).TotalDays >= Math.Max(1, overdueDays);
    }

    /// <summary>纯函数：挑出超期未闭环的待办（严重度倒序、同龄按创建时间从旧到新），并按上限截断。</summary>
    public static (List<MaintenanceRecord> Shown, int Total) SelectOverdue(
        IEnumerable<MaintenanceRecord> all, DateTime now, int overdueDays, int minRank, int cap = TodoMaxListed)
    {
        var hits = (all ?? Enumerable.Empty<MaintenanceRecord>())
            .Where(m => m != null && IsOverdue(m.Status, m.CreatedAt, m.Severity, now, overdueDays, minRank))
            .OrderByDescending(m => SeverityRank(m.Severity))
            .ThenBy(m => m.CreatedAt, StringComparer.Ordinal)
            .ToList();
        return (hits.Take(Math.Max(1, cap)).ToList(), hits.Count);
    }

    /// <summary>纯判定：按天去重（lastPushDay 为空或不等于今天 → 放行）。</summary>
    public static bool DayAllows(string? lastPushDay, string todayKey)
        => !string.Equals(lastPushDay, todayKey, StringComparison.Ordinal);

    // ───────────────────────── 存储健康 ─────────────────────────

    public static void RunStorageCheck(Database db, AppConfig cfg, DateTime? now = null)
    {
        if (db == null || cfg == null || !cfg.FeishuStorageAlertEnabled) return;
        try
        {
            var dayKey = (now ?? DateTime.Now).ToString("yyyy-MM-dd");
            if (!DayAllows(db.GetMeta(MetaStoragePush), dayKey)) return;

            var report = StorageOptimizer.Evaluate(db, cfg);
            if (report.Score >= cfg.FeishuStorageAlertScore) return;

            db.SetMeta(MetaStoragePush, dayKey);
            var station = string.IsNullOrEmpty(cfg.StationId) ? "未知机台" : cfg.StationId;
            var payload = new StorageAlertPayload
            {
                StationId = station,
                Score = report.Score,
                Threshold = cfg.FeishuStorageAlertScore,
                FileBytes = report.FileBytes,
                WalBytes = report.WalBytes,
                BloatRatio = report.BloatRatio,
                Issues = report.Issues.Take(6).ToList(),
                PlannedActions = report.PlannedActions.Take(4).ToList(),
            };
            var url = cfg.WebhookUrl;
            Logger.Warning($"[存储] 健康分 {report.Score}/100 低于阈值 {cfg.FeishuStorageAlertScore}，已推送告警");
            Task.Run(async () =>
            {
                try { await FeishuNotifier.SendStorageAlert(url, payload); }
                catch (Exception ex) { Logger.Error($"[存储] 告警推送失败: {ex.Message}"); }
            });
        }
        catch (Exception ex) { Logger.Warning($"[存储] 告警评估异常（已吞并）: {ex.Message}"); }
    }

    // ───────────────────────── 待办超期 ─────────────────────────

    public static void RunTodoOverdueCheck(Database db, AppConfig cfg, DateTime? now = null)
    {
        if (db == null || cfg == null || !cfg.FeishuTodoOverdueEnabled) return;
        try
        {
            var n = now ?? DateTime.Now;
            var dayKey = n.ToString("yyyy-MM-dd");
            if (!DayAllows(db.GetMeta(MetaTodoPush), dayKey)) return;

            // 审计修复：改用「未闭环 + 创建时间升序」专用查询。原实现取 ListMaintenance("", 5000)
            // （updated_at 倒序的最近 5000 条）再内存筛超期——未处理的老待办排在最末，表一超过 5000 行
            // 就正好被 LIMIT 截掉，最该告警的超期项静默消失。
            var all = db.ListUnclosedMaintenanceAsc(5000);
            var minRank = SeverityRank(cfg.FeishuTodoOverdueMinSeverity);
            var (shown, total) = SelectOverdue(all, n, cfg.FeishuTodoOverdueDays, minRank);
            if (total == 0) return;

            db.SetMeta(MetaTodoPush, dayKey);
            var station = string.IsNullOrEmpty(cfg.StationId) ? "未知机台" : cfg.StationId;
            var payload = new TodoOverduePayload
            {
                StationId = station,
                OverdueDays = cfg.FeishuTodoOverdueDays,
                MinSeverity = cfg.FeishuTodoOverdueMinSeverity,
                Items = shown,
                TotalOverdue = total,
            };
            var url = cfg.WebhookUrl;
            Logger.Warning($"[待办] 超期未闭环 {total} 条（≥{cfg.FeishuTodoOverdueDays} 天，严重度 ≥{cfg.FeishuTodoOverdueMinSeverity}），已推送告警");
            Task.Run(async () =>
            {
                try { await FeishuNotifier.SendTodoOverdueAlert(url, payload); }
                catch (Exception ex) { Logger.Error($"[待办] 超期告警推送失败: {ex.Message}"); }
            });
        }
        catch (Exception ex) { Logger.Warning($"[待办] 超期告警评估异常（已吞并）: {ex.Message}"); }
    }
}
