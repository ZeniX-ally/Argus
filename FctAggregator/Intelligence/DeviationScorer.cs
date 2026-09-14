using System.Text.Json;

namespace FctAggregator;

public sealed class DeviationEvent
{
    public string Ts { get; set; } = "";
    public string Model { get; set; } = "";
    public string Source { get; set; } = "";
    public int SignalCount { get; set; }
    public string TopSignal { get; set; } = "";
    public double TopScore { get; set; }
    public string DetailJson { get; set; } = "";
    public int Seen { get; set; }
}

/// <summary>
/// 正常态偏离评分：单信号 |z| 分 + 四道防误报闸门。开关关 / 非 ready / 投票不足 / 风暴超限一律不落库。
/// </summary>
public static class DeviationScorer
{
    public const double SigmaFloor = 1e-6;
    private static long _stormDropped;

    public static double? ScoreOne(NormalModelRow row, double x, DateTime now, int staleDays)
    {
        if (row == null || !double.IsFinite(x)) return null;
        if (NormalModelStore.EffectiveStatus(row, now, staleDays) != "ready") return null;
        var sigma = Math.Max(row.Sigma, SigmaFloor);
        if (!double.IsFinite(sigma) || sigma <= 0) sigma = SigmaFloor;
        var z = Math.Abs(x - row.Mean) / sigma;
        if (!double.IsFinite(z)) return null;
        return Math.Min(100, Math.Round(z * 100.0 / 6.0));
    }

    public static DeviationEvent? EvaluateWindow(
        Database db, AppConfig cfg, string source, string model, string ts,
        IReadOnlyList<(string SignalKey, double Value)> samples, DateTime now)
    {
        if (cfg == null || !cfg.LearnNormalEnabled || db == null || samples == null || samples.Count == 0)
            return null;
        try
        {
            var hits = new List<(string Key, double Value, double Mean, double Sigma, double Score)>();
            foreach (var (key, value) in samples)
            {
                if (string.IsNullOrWhiteSpace(key) || !double.IsFinite(value)) continue;
                var row = db.GetNormalModel(source, model ?? "", key);
                if (row == null) continue;
                if (string.Equals(source, NormalModelStore.SourceMeasurement, StringComparison.Ordinal)
                    && !CpkAnalyzer.ShouldTreatAsProcess(key, new MeasureStatsRow
                    {
                        TestName = key, Mean = row.Mean, Sigma = row.Sigma, N = (int)Math.Min(int.MaxValue, row.N),
                    }))
                    continue;
                var score = ScoreOne(row, value, now, cfg.LearnNormalStaleDays);
                if (score is not { } s || s < cfg.LearnNormalEventScore) continue;
                hits.Add((key, value, row.Mean, row.Sigma, s));
            }
            if (hits.Count < Math.Max(1, cfg.LearnNormalVoteSignals)) return null;

            var day = (ts.Length >= 10 ? ts[..10] : now.ToString("yyyy-MM-dd"));
            if (db.CountDeviationEventsOnDay(day) >= Math.Max(1, cfg.LearnNormalMaxEventsPerDay))
            {
                Interlocked.Increment(ref _stormDropped);
                return null;
            }

            var top = hits.OrderByDescending(h => h.Score).First();
            var ev = new DeviationEvent
            {
                Ts = string.IsNullOrWhiteSpace(ts) ? now.ToString("yyyy-MM-dd HH:mm:ss") : ts,
                Model = model ?? "",
                Source = source,
                SignalCount = hits.Count,
                TopSignal = top.Key,
                TopScore = top.Score,
                DetailJson = JsonSerializer.Serialize(hits.Select(h => new
                {
                    signal_key = h.Key, score = h.Score, value = h.Value, mean = h.Mean, sigma = h.Sigma,
                })),
            };
            db.InsertDeviationEvent(ev);
            // 达分数下限且当日未超推送封顶时推飞书（PASS 但在偏离 = 比 FAIL 更早的预警）
            LearnAlertMonitor.MaybeNotifyDeviation(db, cfg, ev);
            return ev;
        }
        catch (Exception ex)
        {
            Logger.Warning($"[正常态] 偏离评分异常(已吞并): {ex.Message}");
            return null;
        }
    }

    public static void EvaluateMeasurementRecord(Database db, AppConfig cfg, TestRecord rec)
    {
        if (cfg == null || !cfg.LearnNormalEnabled || rec == null) return;
        if (!string.Equals(rec.Result, "PASS", StringComparison.OrdinalIgnoreCase)) return;
        var samples = rec.Measurements
            .Where(m => m.Value is { } v && double.IsFinite(v) && !string.IsNullOrWhiteSpace(m.TestName))
            .Select(m => (m.TestName, m.Value!.Value))
            .ToList();
        var ts = string.IsNullOrWhiteSpace(rec.BatchTimestamp) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : rec.BatchTimestamp;
        EvaluateWindow(db, cfg, NormalModelStore.SourceMeasurement, rec.Model ?? "", ts, samples, DateTime.Now);
    }

    public static void EvaluateWaveform(Database db, AppConfig cfg, TestRecord rec, List<TdmsFeatureAnalyzer.TdmsFeatureRow> rows)
    {
        if (cfg == null || !cfg.LearnNormalEnabled || rec == null || rows == null) return;
        if (!string.Equals(rec.Result, "PASS", StringComparison.OrdinalIgnoreCase)) return;
        var samples = new List<(string, double)>();
        foreach (var f in rows)
        {
            if (string.IsNullOrWhiteSpace(f.GroupName) || string.IsNullOrWhiteSpace(f.ChannelName)) continue;
            var baseKey = $"{f.GroupName}/{f.ChannelName}";
            if (f.Mean is { } vm && double.IsFinite(vm)) samples.Add((baseKey + "/vmean", vm));
            if (f.Std is { } vs && double.IsFinite(vs)) samples.Add((baseKey + "/vstd", vs));
            if (f.Max is { } vx && double.IsFinite(vx)) samples.Add((baseKey + "/vmax", vx));
        }
        var ts = string.IsNullOrWhiteSpace(rec.BatchTimestamp) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : rec.BatchTimestamp;
        EvaluateWindow(db, cfg, NormalModelStore.SourceWaveform, rec.Model ?? "", ts, samples, DateTime.Now);
    }

    public static void EvaluateDeviceSample(Database db, AppConfig cfg, double cpu, double memPct, double diskFreeGb, string? ts = null)
    {
        if (cfg == null || !cfg.LearnNormalEnabled) return;
        var t = string.IsNullOrWhiteSpace(ts) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : ts;
        if (t.Length < 13) t = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var slot = TimeSlot.SlotOfHour(int.Parse(t.Substring(11, 2)));
        var samples = new List<(string, double)>();
        if (double.IsFinite(cpu)) samples.Add(($"cpu@{slot}", cpu));
        if (double.IsFinite(memPct)) samples.Add(($"mem@{slot}", memPct));
        if (double.IsFinite(diskFreeGb)) samples.Add(($"disk@{slot}", diskFreeGb));
        EvaluateWindow(db, cfg, NormalModelStore.SourceDevice, "", t, samples, DateTime.Now);
    }

    public static long StormDropped => Interlocked.Read(ref _stormDropped);
}
