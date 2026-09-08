using System.Globalization;

namespace FctAggregator;

public static class PredictAccuracyReconciler
{
    public const int DefaultHorizonDays = 14;

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

    private static int ReconcileAlertPredicts(AggDatabase db, AppConfig cfg)
    {
        var horizon = Math.Max(1, cfg.PredictReconcileHorizonDays);
        var since = DateTime.Now.AddDays(-horizon).ToString("yyyy-MM-dd HH:mm:ss");
        var upto = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd HH:mm:ss");

        var rows = QueryAlertPredictsRange(db, since, upto);
        var count = 0;
        foreach (var p in rows)
        {
            if (string.IsNullOrEmpty(p.machine) || string.IsNullOrEmpty(p.rule)) continue;
            if (db.PredictAccuracyExists("alert", p.id)) continue;

            if (!TryParseTs(p.ts, out var predictedAt)) continue;
            var actual = LookupYieldActual(db, p.machine, predictedAt);
            if (actual == null) continue;

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

    public sealed class ActualValuePublic
    {
        public double Value;
        public DateTime EventTs;
        public string Note = "";
    }

    private static ActualValuePublic? LookupYieldActual(AggDatabase db, string machine, DateTime predictedAt)
    {
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

    public static (bool Hit, double LeadDays) ClassifyYieldHit(ActualValuePublic actual,
        (long id, string ts, string machine, string rule, string level, double current, double predicted, string detail) predict,
        AppConfig cfg)
    {
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
        var hit = actual.Value >= 95.0;
        var lead = (actual.EventTs - DateTime.Now.AddDays(-1)).TotalDays;
        return (hit, Math.Round(lead, 2));
    }

    public static (bool Hit, double LeadDays) ClassifyDiskHit(ActualValuePublic actual, double predicted)
    {
        var hit = actual.Value <= 5.0;
        var lead = 0.0;
        return (hit, lead);
    }

    private static (bool Hit, double LeadDays, double? Actual, string Note)
        ClassifyOfflineHit(AggDatabase db,
            (long id, string ts, string machine, string metric, string level, double predicted, int? days, string detail) predict,
            DateTime predictedAt)
    {
        var info = db.GetDeviceInfo(predict.machine);
        if (info == null || string.IsNullOrEmpty(info.LastSeen)) return (false, 0, null, "no device_info");
        if (!TryParseTs(info.LastSeen, out var lastDt)) return (false, 0, null, "bad last_seen");
        var gap = (DateTime.Now - lastDt).TotalSeconds;
        var hit = gap > 90;
        return (hit, Math.Round((DateTime.Now - predictedAt).TotalDays, 2), gap, $"gap={gap:0}s");
    }

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

        if (cfg.PredictTuneEnabled && !newEmpty)
        {
            BuildThresholdTuning(summary, cfg);
        }

        summary.GeneratedAt = DateTime.Now;
        return summary;
    }

    private static void BuildThresholdTuning(ReconcileSummary summary, AppConfig cfg)
    {
        RecommendYield(summary, cfg);
    }

    private static void RecommendYield(ReconcileSummary summary, AppConfig cfg)
    {
        if (!summary.Summary.TryGetValue("yield", out var stat)) return;
        var current = cfg.YieldAlertYieldPct;
        if (stat.Total < cfg.PredictTuneMinSamples) return;

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
        _ => 0.0,
    };

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
