using System.Text.Json;

namespace FctAggregator;

/// <summary>采集异常告警的状态（落 app_meta，跨重启保持）：避免重复推送、支持「已恢复」通知。</summary>
public sealed class CollectAlertState
{
    public bool Alerting { get; set; }
    public string LastPushTs { get; set; } = "";
    public string LastReason { get; set; } = "";

    public string ToJson() => JsonSerializer.Serialize(this);

    public static CollectAlertState? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<CollectAlertState>(json); }
        catch { return null; }
    }
}

public enum CollectAlertDecision { None, Alert, Recovered }

/// <summary>采集异常告警卡的载荷。</summary>
public sealed class CollectAlertPayload
{
    public string StationId { get; set; } = "";
    public int WindowMin { get; set; }
    public int AbnormalCount { get; set; }
    public int ParseThreshold { get; set; }
    public int RetryQueueDepth { get; set; }
    public int RetryThreshold { get; set; }
    public List<CollectFailureRow> ByCode { get; set; } = new();
    public List<CollectFailureItem> Recent { get; set; } = new();
}

/// <summary>每日运行摘要卡的载荷（复用 DbMaintenance.RunNow 已有结果，不额外计算）。</summary>
public sealed class DailySummaryPayload
{
    public string Date { get; set; } = "";
    public string StationId { get; set; } = "";
    public int Total { get; set; }
    public int Pass { get; set; }
    public int Fail { get; set; }
    public int Interrupted { get; set; }
    public double YieldPct { get; set; }
    public List<TopFailItem> TopFails { get; set; } = new();
    public int StorageScore { get; set; }
    public long DbBytes { get; set; }
    public string BackupName { get; set; } = "";
    public bool BackupOk { get; set; }
    public int RetryQueueDepth { get; set; }
    public int AbnormalParseCount { get; set; }
    public int WindowMin { get; set; }
}

/// <summary>
/// 采集健康度巡检：把「漏采」从"只能翻日志"变成主动告警。
///
/// 为什么值得单独做一张卡：漏采是**数据正确性**问题——FAIL 文件没进库会让良率偏高，
/// 而现场完全没有感知（`parse_failure_log` 自审计 A6/ALGO-2 起就在写，但一直没有读取入口）。
///
/// 口径：
/// - 两个触发维度，任一越线即告警：窗口内**异常**解析失败条数（排除 `skip`）、重试队列积压条数；
/// - 阈值 ≤ 0 视为关闭该维度；
/// - 节流：同一告警最短间隔 `feishu_collect_alert_throttle_min` 分钟，防止持续异常时刷屏；
/// - 恢复正常时补发一条「已恢复」——否则运维不知道该不该继续管。
/// </summary>
public static class CollectHealthMonitor
{
    public const string MetaState = "feishu_collect_alert_state";
    /// <summary>每日摘要已推送日期（yyyyMMdd）。RunNow 每次进程启动都会补跑，靠它保证一天只推一次。</summary>
    public const string MetaLastSummaryDay = "feishu_daily_summary_last_day";

    /// <summary>纯判定：该不该告警 / 该不该报恢复。阈值 ≤0 表示关闭该维度。</summary>
    public static CollectAlertDecision Decide(bool wasAlerting, int abnormalCount, int parseThreshold,
        int retryDepth, int retryThreshold)
    {
        var parseBad = parseThreshold > 0 && abnormalCount >= parseThreshold;
        var retryBad = retryThreshold > 0 && retryDepth >= retryThreshold;
        if (parseBad || retryBad) return CollectAlertDecision.Alert;
        return wasAlerting ? CollectAlertDecision.Recovered : CollectAlertDecision.None;
    }

    /// <summary>纯判定：节流是否放行。throttleMinutes ≤0 表示不节流；时间戳为空/不可解析视为放行。</summary>
    public static bool ThrottleAllows(string? lastPushTs, DateTime now, int throttleMinutes)
    {
        if (throttleMinutes <= 0) return true;
        if (string.IsNullOrWhiteSpace(lastPushTs)) return true;
        if (!DateTime.TryParse(lastPushTs, out var last)) return true;
        return (now - last).TotalMinutes >= throttleMinutes;
    }

    /// <summary>纯判定：告警原因文案（卡面与日志共用，避免两处各写一份）。</summary>
    public static string ReasonOf(int abnormalCount, int parseThreshold, int retryDepth, int retryThreshold)
    {
        var parts = new List<string>();
        if (parseThreshold > 0 && abnormalCount >= parseThreshold)
            parts.Add($"窗口内解析失败 {abnormalCount} 条（阈值 {parseThreshold}）");
        if (retryThreshold > 0 && retryDepth >= retryThreshold)
            parts.Add($"重试队列积压 {retryDepth} 条（阈值 {retryThreshold}）");
        return parts.Count == 0 ? "" : string.Join("；", parts);
    }

    public static string WindowStart(DateTime now, int windowMin)
        => now.AddMinutes(-Math.Clamp(windowMin, 5, 1440)).ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>每日摘要统计的日期口径：维护任务默认凌晨执行（db_maintenance_hour=3），
    /// 此刻「今天」几乎没有数据，应汇总刚结束的昨天；若维护时间设在当天 12 点之后，则汇总当天。</summary>
    public static DateTime SummaryDayOf(DateTime now) => now.Hour < 12 ? now.Date.AddDays(-1) : now.Date;

    /// <summary>巡检一次（由 Engine 的定时器调用）。DB 查询同步完成，推送异步发出，不阻塞调用线程。</summary>
    public static void RunOnce(Database db, AppConfig cfg, int retryQueueDepth, DateTime? now = null)
    {
        if (db == null || cfg == null) return;
        if (!cfg.FeishuCollectAlertEnabled) return;

        try
        {
            var n = now ?? DateTime.Now;
            var state = CollectAlertState.FromJson(db.GetMeta(MetaState)) ?? new CollectAlertState();
            var station = string.IsNullOrEmpty(cfg.StationId) ? null : cfg.StationId;
            var since = WindowStart(n, cfg.FeishuCollectAlertWindowMin);
            var abnormal = db.CountAbnormalParseFailures(since, station);

            var decision = Decide(state.Alerting, abnormal, cfg.FeishuCollectAlertParseMin,
                retryQueueDepth, cfg.FeishuCollectAlertRetryMin);

            if (decision == CollectAlertDecision.Alert)
            {
                if (!ThrottleAllows(state.LastPushTs, n, cfg.FeishuCollectAlertThrottleMin)) return;

                var payload = new CollectAlertPayload
                {
                    StationId = string.IsNullOrEmpty(cfg.StationId) ? "未知机台" : cfg.StationId,
                    WindowMin = cfg.FeishuCollectAlertWindowMin,
                    AbnormalCount = abnormal,
                    ParseThreshold = cfg.FeishuCollectAlertParseMin,
                    RetryQueueDepth = retryQueueDepth,
                    RetryThreshold = cfg.FeishuCollectAlertRetryMin,
                    ByCode = db.FetchParseFailureSummary(since, station),
                    Recent = db.FetchRecentParseFailures(5, since, station),
                };

                // 先落状态再发：避免发送耗时期间下一轮重复触发（发送失败则等下一个节流窗口重报，
                // 条件本身是持续的，不会因为这一次失败而漏掉）
                state.Alerting = true;
                state.LastPushTs = n.ToString("yyyy-MM-dd HH:mm:ss");
                state.LastReason = ReasonOf(abnormal, cfg.FeishuCollectAlertParseMin, retryQueueDepth, cfg.FeishuCollectAlertRetryMin);
                db.SetMeta(MetaState, state.ToJson());

                Logger.Warning($"[采集健康] 告警: {state.LastReason}");
                var url = cfg.WebhookUrl;
                Task.Run(async () =>
                {
                    try { await FeishuNotifier.SendCollectAlert(url, payload); }
                    catch (Exception ex) { Logger.Error($"[采集健康] 告警推送失败: {ex.Message}"); }
                });
            }
            else if (decision == CollectAlertDecision.Recovered)
            {
                var recoveredFrom = state.LastReason;
                state.Alerting = false;
                state.LastPushTs = n.ToString("yyyy-MM-dd HH:mm:ss");
                state.LastReason = "";
                db.SetMeta(MetaState, state.ToJson());

                Logger.Info($"[采集健康] 已恢复（此前: {recoveredFrom}）");
                var url = cfg.WebhookUrl;
                Task.Run(async () =>
                {
                    try { await FeishuNotifier.SendCollectRecover(url, cfg.StationId, recoveredFrom, abnormal); }
                    catch (Exception ex) { Logger.Error($"[采集健康] 恢复通知推送失败: {ex.Message}"); }
                });
            }
        }
        catch (Exception ex) { Logger.Warning($"[采集健康] 巡检异常（已吞并）: {ex.Message}"); }
    }

    /// <summary>组装每日摘要载荷（纯函数：入参已算好，便于自检直接断言卡面）。</summary>
    public static DailySummaryPayload BuildDailySummary(string stationId, DateTime day, StatsData stats,
        List<TopFailItem> topFails, int storageScore, long dbBytes, string? backupName,
        int retryQueueDepth, int abnormalParseCount, int windowMin)
    {
        var total = stats.Pass + stats.Fail;
        return new DailySummaryPayload
        {
            Date = day.ToString("yyyy-MM-dd"),
            StationId = string.IsNullOrEmpty(stationId) ? "未知机台" : stationId,
            Total = total,
            Pass = stats.Pass,
            Fail = stats.Fail,
            Interrupted = stats.Interrupted,
            YieldPct = Math.Round(StatsUtil.YieldPct(stats.Pass, stats.Fail), 2),
            TopFails = topFails ?? new List<TopFailItem>(),
            StorageScore = storageScore,
            DbBytes = dbBytes,
            BackupName = backupName == null ? "" : Path.GetFileName(backupName),
            BackupOk = backupName != null,
            RetryQueueDepth = retryQueueDepth,
            AbnormalParseCount = abnormalParseCount,
            WindowMin = windowMin,
        };
    }

    /// <summary>发送每日摘要（由 DbMaintenance 在每日任务跑完后调用）。</summary>
    public static void SendDailySummary(AppConfig cfg, DailySummaryPayload payload)
    {
        if (cfg == null || payload == null || !cfg.FeishuDailySummaryEnabled) return;
        var url = cfg.WebhookUrl;
        Task.Run(async () =>
        {
            try { await FeishuNotifier.SendDailySummary(url, payload); }
            catch (Exception ex) { Logger.Error($"[每日摘要] 推送失败: {ex.Message}"); }
        });
    }
}
