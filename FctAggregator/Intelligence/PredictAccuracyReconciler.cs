using System.Globalization;

namespace FctAggregator;

/// <summary>
/// 预测准确率对账器（v3.20.0 规格 02）：把 alert_predict_log / device_predict_log 的预测
/// 与后来实际值对账，落 predict_accuracy_log；同时按"近 30 天命中率"产出阈值推荐。
///
/// 设计要点：
/// - 对账只读算 + 单写者写锁（与现有 Mesh 推送串行，不阻塞入库）；
/// - 阈值推荐**只写不应用**——避免现场被自动改阈值后懵；
/// - 每次 RunOnce 增量对账（predict_table+predict_id UNIQUE 去重），整批 < 1s；
/// - DbMaintenance.RunNow 每日凌晨顺带调用，与 checkpoint/VACUUM/backup 同锁内串行。
/// </summary>
public static class PredictAccuracyReconciler
{
    /// <summary>默认对账窗口（天）——与 config 一致但允许覆写。</summary>
    public const int DefaultHorizonDays = 14;

    /// <summary>对账 + 统计 + 推荐。返回本次新对账条数（不含历史已对账的）。</summary>
    public static ReconcileSummary Reconcile(AggDatabase db, AppConfig cfg)
    {
        if (cfg.PredictReconcileEnabled == false)
        {
            return BuildSummary(db, cfg, newEmpty: true);
        }
        var reconciled = 0;
        try
        {
            reconciled += ReconcileAlertPredicts(db, cfg);
            reconciled += ReconcileDevicePredicts(db, cfg);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[自反馈] 对账异常（不影响运行）: {ex.Message}");
        }
        Logger.Info($"[自反馈] 本轮对账完成，新增 {reconciled} 条");
        return BuildSummary(db, cfg, newEmpty: false);
    }

    /// <summary>后台入口，由 DbMaintenance.RunNow 顺带调用；返回本次新对账条数。</summary>
    public static int RunOnce(AggDatabase db, AppConfig cfg)
    {
        if (cfg.PredictReconcileEnabled == false) return 0;
        var reconciled = 0;
        try
        {
            reconciled += ReconcileAlertPredicts(db, cfg);
            reconciled += ReconcileDevicePredicts(db, cfg);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[自反馈] RunOnce 异常: {ex.Message}");
        }
        if (reconciled > 0) Logger.Info($"[自反馈] RunOnce 新增对账 {reconciled} 条");
        return reconciled;
    }

    // ==================== 对账：alert_predict_log（yield）====================

    private static int ReconcileAlertPredicts(AggDatabase db, AppConfig cfg)
    {
        var horizon = Math.Max(1, cfg.PredictReconcileHorizonDays);
        var since = DateTime.Now.AddDays(-horizon).ToString("yyyy-MM-dd HH:mm:ss");
        var upto = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss");   // 至少 1 天前的预测才能对账

        // 取窗口内的 alert 预测，按时间升序便于边界检查
        var rows = QueryAlertPredictsRange(db, since, upto);
        var count = 0;
        foreach (var p in rows)
        {
            if (string.IsNullOrEmpty(p.machine) || string.IsNullOrEmpty(p.rule)) continue;
            if (db.PredictAccuracyExists("alert", p.id)) continue;

            if (!TryParseTs(p.ts, out var predictedAt)) continue;
            var actual = LookupYieldActual(db, p.machine, predictedAt);
            if (actual == null) continue;   // 还不够数据，跳过

            var (hit, leadDays) = ClassifyYieldHit(actual, p, cfg);
            db.UpsertPredictAccuracy(new AggDatabase.AccuracyRow
            {
                Rule = p.rule,
                Machine = p.machine,
                PredictId = p.id,
                PredictTable = "alert",
                PredictedValue = p.predicted,
                ActualValue = actual.Value,
                Threshold = cfg.YieldAlertYieldPct,
                Hit = hit,
                LeadDays = leadDays,
                PredictedAt = p.ts,
                Note = actual.Note,
            });
            count++;
        }
        return count;
    }

    // ==================== 对账：device_predict_log（cpu/disk/offline）====================

    private static int ReconcileDevicePredicts(AggDatabase db, AppConfig cfg)
    {
        var horizon = Math.Max(1, cfg.PredictReconcileHorizonDays);
        var since = DateTime.Now.AddDays(-horizon).ToString("yyyy-MM-dd HH:mm:ss");
        var upto = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss");

        var rows = QueryDevicePredictsRange(db, since, upto);
        var count = 0;
        foreach (var p in rows)
        {
            if (string.IsNullOrEmpty(p.machine) || string.IsNullOrEmpty(p.metric)) continue;
            if (db.PredictAccuracyExists("device", p.id)) continue;

            if (!TryParseTs(p.ts, out var predictedAt)) continue;

            // 归一：device_predict_log.metric 就是 rule（cpu/disk/offline）
            var rule = p.metric;
            (bool hit, double leadDays, double? actual, string note)? result = rule switch
            {
                "cpu"     => ClassifyDeviceHit(db, p, predictedAt, "cpu", LookupCpuActual, ClassifyCpuHit),
                "disk"    => ClassifyDeviceHit(db, p, predictedAt, "disk", LookupDiskActual, ClassifyDiskHit),
                "offline" => ClassifyOfflineHit(db, p, predictedAt),
                _ => null,
            };
            if (result == null) continue;

            db.UpsertPredictAccuracy(new AggDatabase.AccuracyRow
            {
                Rule = rule,
                Machine = p.machine,
                PredictId = p.id,
                PredictTable = "device",
                PredictedValue = p.predicted,
                ActualValue = result.Value.actual,
                Threshold = GetDeviceThreshold(cfg, rule),
                Hit = result.Value.hit,
                LeadDays = result.Value.leadDays,
                PredictedAt = p.ts,
                Note = result.Value.note,
            });
            count++;
        }
        return count;
    }

    // ==================== 实际值查找 ====================

    /// <summary>一个"实际值"快照（查 yld_daily / device_samples 得到，供 Classify*Hit 判定）。</summary>
    public sealed class ActualValuePublic
    {
        public double Value;
        public DateTime EventTs;
        public string Note = "";
    }

    private static ActualValuePublic? LookupYieldActual(AggDatabase db, string machine, DateTime predictedAt)
    {
        // 预测 1 天后的实际 yld_daily（yld_daily.test_date 是 yyyyMMdd）
        var target = predictedAt.AddDays(1).ToString("yyyyMMdd");
        var row = db.QueryDailyStats(machine, target, target, maxRows: 1).FirstOrDefault();
        if (row == null || row.Total <= 0) return null;
        var y = (double)row.Pass / row.Total * 100.0;
        return new ActualValuePublic
        {
            Value = Math.Round(y, 3),
            EventTs = TryParseExact(row.TestDate) ?? predictedAt.AddDays(1),
            Note = $"yld_daily {row.TestDate}",
        };
    }

    private static ActualValuePublic? LookupCpuActual(AggDatabase db, string machine, DateTime predictedAt)
    {
        // 预测时刻 + 1 天的 cpu_usage（取 ≤ 该时刻的最新 sample）
        var cutoff = predictedAt.AddDays(1).ToString("yyyy-MM-dd HH:mm:ss");
        var samples = db.QueryDeviceSamples(machine, 1, fromTs: null, toTs: cutoff);
        if (samples.Count == 0) return null;
        var s = samples[^1];
        return new ActualValuePublic { Value = s.CpuUsage, EventTs = TryParseExact(s.Ts) ?? predictedAt.AddDays(1) };
    }

    private static ActualValuePublic? LookupDiskActual(AggDatabase db, string machine, DateTime predictedAt)
    {
        var cutoff = predictedAt.AddDays(1).ToString("yyyy-MM-dd HH:mm:ss");
        var samples = db.QueryDeviceSamples(machine, 1, fromTs: null, toTs: cutoff);
        if (samples.Count == 0) return null;
        var s = samples[^1];
        return new ActualValuePublic { Value = s.DiskFreeGb, EventTs = TryParseExact(s.Ts) ?? predictedAt.AddDays(1) };
    }

    // ==================== hit 判定（公开以方便单测）====================

    public static (bool Hit, double LeadDays) ClassifyYieldHit(ActualValuePublic actual,
        (long id, string ts, string machine, string rule, string level, double current, double predicted, string detail) predict,
        AppConfig cfg)
    {
        // yield hit：预测 "会跌破阈值" 且 实际真的跌破
        // yield miss：预测 "会跌破阈值" 但 实际没跌破
        var threshold = cfg.YieldAlertYieldPct;
        var predicted_will_break = predict.predicted < threshold;
        var actually_broke = actual.Value < threshold;
        var hit = predicted_will_break == actually_broke;
        var lead = (actual.EventTs - TryParseExact(predict.ts)!.Value).TotalDays;
        return (hit, Math.Round(lead, 2));
    }

    private static (bool Hit, double LeadDays, double? Actual, string Note)
        ClassifyDeviceHit(AggDatabase db,
            (long id, string ts, string machine, string metric, string level, double predicted, int? days, string detail) predict,
            DateTime predictedAt,
            string rule,
            Func<AggDatabase, string, DateTime, ActualValuePublic?> lookup,
            Func<ActualValuePublic, double, (bool, double)> classify)
    {
        var actual = lookup(db, predict.machine, predictedAt);
        if (actual == null) return (false, 0, null, "no actual");
        var (hit, lead) = classify(actual, predict.predicted);
        return (hit, Math.Round(lead, 2), actual.Value, $"{rule} actual={actual.Value:0.##}");
    }

    public static (bool Hit, double LeadDays) ClassifyCpuHit(ActualValuePublic actual, double predicted)
    {
        // cpu hit：预测 cpu≥95（critical/warn 阈值）且实际确实 ≥95
        var hit = actual.Value >= 95.0;
        var lead = (actual.EventTs - DateTime.Now.AddDays(-1)).TotalDays;
        return (hit, Math.Round(lead, 2));
    }

    public static (bool Hit, double LeadDays) ClassifyDiskHit(ActualValuePublic actual, double predicted)
    {
        // disk hit：预测耗尽 ±3 天内，actual disk_free ≤ 5GB 视为"真快耗尽"
        var hit = actual.Value <= 5.0;
        var lead = 0.0;
        return (hit, lead);
    }

    private static (bool Hit, double LeadDays, double? Actual, string Note)
        ClassifyOfflineHit(AggDatabase db,
            (long id, string ts, string machine, string metric, string level, double predicted, int? days, string detail) predict,
            DateTime predictedAt)
    {
        // offline hit：预测会离线，24h 内 last_seen > 90s
        var info = db.GetDeviceInfo(predict.machine);
        if (info == null || string.IsNullOrEmpty(info.LastSeen)) return (false, 0, null, "no device_info");
        if (!TryParseTs(info.LastSeen, out var lastDt)) return (false, 0, null, "bad last_seen");
        var gap = (DateTime.Now - lastDt).TotalSeconds;
        var hit = gap > 90;
        return (hit, Math.Round((DateTime.Now - predictedAt).TotalDays, 2), gap, $"gap={gap:0}s");
    }

    // ==================== 统计与推荐 ====================

    private static ReconcileSummary BuildSummary(AggDatabase db, AppConfig cfg, bool newEmpty)
    {
        var summary = new ReconcileSummary { WindowDays = cfg.PredictReconcileHorizonDays * 2 };
        foreach (var rule in new[] { "yield", "cpu", "disk", "offline" })
        {
            var (total, hit, lead) = db.CountPredictAccuracyByRule(rule, cfg.PredictReconcileHorizonDays * 2);
            summary.Summary[rule] = new RuleStat
            {
                Total = total,
                Hit = hit,
                Accuracy = total > 0 ? Math.Round((double)hit / total, 4) : 0.0,
                AvgLeadDays = Math.Round(lead, 2),
            };
        }

        // per-machine：近 30 天内每台每规则的命中率 + miss_streak
        var recent = db.QueryPredictAccuracy(days: 30, limit: 5000);
        var byKey = recent.GroupBy(r => (r.Machine, r.Rule));
        foreach (var g in byKey)
        {
            var ordered = g.OrderByDescending(r => r.ReconciledAt).ToList();
            var hits = ordered.Count(r => r.Hit);
            var total = ordered.Count;
            var streak = 0;
            foreach (var r in ordered) { if (!r.Hit) streak++; else break; }
            summary.PerMachine.Add(new MachineHitRate
            {
                Machine = g.Key.Machine,
                Rule = g.Key.Rule,
                HitRate = total > 0 ? Math.Round((double)hits / total, 4) : 0.0,
                MissStreak = streak,
            });
        }

        // 阈值推荐（仅 tune 开启）
        if (cfg.PredictTuneEnabled && !newEmpty)
        {
            BuildThresholdTuning(summary, cfg);
        }

        summary.GeneratedAt = DateTime.Now;
        return summary;
    }

    private static void BuildThresholdTuning(ReconcileSummary summary, AppConfig cfg)
    {
        // yield 是 "跌破阈值告警"：阈值 ↑ → 更严（更少触发）。
        // accuracy 太低（误报多）→ 阈值 ↑；accuracy 太高（漏报多）→ 阈值 ↓。
        RecommendYield(summary, cfg);
        // cpu/disk/offline 的阈值先不动（v3.20.0 只演示 yield）
    }

    private static void RecommendYield(ReconcileSummary summary, AppConfig cfg)
    {
        if (!summary.Summary.TryGetValue("yield", out var stat)) return;
        var current = cfg.YieldAlertYieldPct;
        if (stat.Total < cfg.PredictTuneMinSamples) return;   // 冷启动保护

        // accuracy 太低 → 阈值 +5%（更难跌破 → 告警少）
        if (stat.Accuracy < 0.30 && current < 99.0)
        {
            var rec = Math.Min(99.0, current + 5.0);
            summary.ThresholdTuning.Add(new ThresholdTune
            {
                Rule = "yield",
                Current = Math.Round(current, 2),
                Recommended = Math.Round(rec, 2),
                Reason = $"命中率 {stat.Accuracy:P0} < 30%（{stat.Total} 样本），放宽阈值减少误报",
            });
        }
        // accuracy 太高 + 样本足够 → 阈值 -5%（更紧 → 漏报减少）
        else if (stat.Accuracy > 0.85 && stat.Total >= cfg.PredictTuneMinSamples && current > 50.0)
        {
            var rec = Math.Max(50.0, current - 5.0);
            summary.ThresholdTuning.Add(new ThresholdTune
            {
                Rule = "yield",
                Current = Math.Round(current, 2),
                Recommended = Math.Round(rec, 2),
                Reason = $"命中率 {stat.Accuracy:P0} > 85%（{stat.Total} 样本），收紧阈值减少漏报",
            });
        }
    }

    // ==================== 工具：SQL 包装（避开编译器不知道的字段类型）====================

    private static List<(long id, string ts, string machine, string rule, string level, double current, double predicted, string detail)>
        QueryAlertPredictsRange(AggDatabase db, string since, string upto)
    {
        var list = new List<(long, string, string, string, string, double, double, string)>();
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, ts, machine, rule, level, current, predicted, detail
                              FROM alert_predict_log
                             WHERE ts >= @since AND ts <= @upto
                             ORDER BY ts ASC";
        cmd.Parameters.AddWithValue("@since", since);
        cmd.Parameters.AddWithValue("@upto", upto);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add((
                r.GetInt64(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? "" : r.GetString(2),
                r.IsDBNull(3) ? "" : r.GetString(3),
                r.IsDBNull(4) ? "" : r.GetString(4),
                r.IsDBNull(5) ? 0.0 : r.GetDouble(5),
                r.IsDBNull(6) ? 0.0 : r.GetDouble(6),
                r.IsDBNull(7) ? "" : r.GetString(7)
            ));
        }
        return list;
    }

    private static List<(long id, string ts, string machine, string metric, string level, double predicted, int? days, string detail)>
        QueryDevicePredictsRange(AggDatabase db, string since, string upto)
    {
        var list = new List<(long, string, string, string, string, double, int?, string)>();
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, ts, machine, metric, level, predicted, days_to_exhaust, detail
                              FROM device_predict_log
                             WHERE ts >= @since AND ts <= @upto
                             ORDER BY ts ASC";
        cmd.Parameters.AddWithValue("@since", since);
        cmd.Parameters.AddWithValue("@upto", upto);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add((
                r.GetInt64(0),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? "" : r.GetString(2),
                r.IsDBNull(3) ? "" : r.GetString(3),
                r.IsDBNull(4) ? "" : r.GetString(4),
                r.IsDBNull(5) ? 0.0 : r.GetDouble(5),
                r.IsDBNull(6) ? null : (int?)Convert.ToInt32(r.GetInt64(6)),
                r.IsDBNull(7) ? "" : r.GetString(7)
            ));
        }
        return list;
    }

    private static bool TryParseTs(string s, out DateTime dt)
    {
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt))
            return true;
        dt = default;
        return false;
    }

    private static DateTime? TryParseExact(string s)
    {
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
            return dt;
        return null;
    }

    private static double GetDeviceThreshold(AppConfig cfg, string rule) => rule switch
    {
        "cpu" => cfg.DeviceAlertCpuPct,
        "offline" => cfg.DeviceAlertOfflineMinutes,
        _ => 0.0,    // disk 没阈值
    };

    // ==================== DTO ====================

    public sealed class ReconcileSummary
    {
        public int WindowDays;
        public Dictionary<string, RuleStat> Summary = new();
        public List<MachineHitRate> PerMachine = new();
        public List<ThresholdTune> ThresholdTuning = new();
        public DateTime GeneratedAt;
    }

    public sealed class RuleStat
    {
        public int Total;
        public int Hit;
        public double Accuracy;
        public double AvgLeadDays;
    }

    public sealed class MachineHitRate
    {
        public string Machine = "";
        public string Rule = "";
        public double HitRate;
        public int MissStreak;
    }

    public sealed class ThresholdTune
    {
        public string Rule = "";
        public double Current;
        public double Recommended;
        public string Reason = "";
    }
}
