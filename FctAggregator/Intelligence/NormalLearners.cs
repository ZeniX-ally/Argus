using System.Text.Json;

namespace FctAggregator;

/// <summary>
/// 正常态自学习入口：三个学习器（测量/波形/设备行为）。
/// 铁律：开关关=纯 no-op；只学 PASS；只学有限值；吞异常只计数，绝不阻塞摄取链路。
/// </summary>
public static class NormalLearners
{
    public const string MetaHeartbeat = "learn_normal_heartbeat";
    private const int FlushEveryUpdates = 200;

    private static readonly object _hbLock = new();
    private static long _mUpd, _wUpd, _dUpd, _mErr, _wErr, _dErr;
    private static long _sinceFlush;

    public static void ObserveMeasurement(Database db, AppConfig cfg, TestRecord rec)
    {
        if (!cfg.LearnNormalEnabled || db == null) return;
        try
        {
            if (!string.Equals(rec.Result, "PASS", StringComparison.OrdinalIgnoreCase)) return;
            var ts = string.IsNullOrWhiteSpace(rec.BatchTimestamp)
                ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                : TimeUtil.Normalize(rec.BatchTimestamp);
            if (ts.Length == 0) ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var model = rec.Model ?? "";
            foreach (var m in rec.Measurements)
            {
                if (m.Value is not { } v || !double.IsFinite(v)) continue;
                if (string.IsNullOrWhiteSpace(m.TestName)) continue;
                NormalModelStore.Observe(db, cfg.LearnNormalMinSamples, NormalModelStore.SourceMeasurement, model, m.TestName, v, ts);
                Interlocked.Increment(ref _mUpd);
            }
            MaybeFlush(db);
        }
        catch (Exception ex) { Interlocked.Increment(ref _mErr); Logger.Warning($"[正常态] 测量学习异常(已吞并): {ex.Message}"); }
    }

    public static void ObserveWaveform(Database db, AppConfig cfg, TestRecord rec, List<TdmsFeatureAnalyzer.TdmsFeatureRow> rows)
    {
        if (!cfg.LearnNormalEnabled || db == null || rows == null || rows.Count == 0) return;
        try
        {
            if (!string.Equals(rec.Result, "PASS", StringComparison.OrdinalIgnoreCase)) return;
            var ts = string.IsNullOrWhiteSpace(rec.BatchTimestamp)
                ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                : TimeUtil.Normalize(rec.BatchTimestamp);
            if (ts.Length == 0) ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var model = rec.Model ?? "";
            foreach (var f in rows)
            {
                if (string.IsNullOrWhiteSpace(f.GroupName) || string.IsNullOrWhiteSpace(f.ChannelName)) continue;
                var baseKey = $"{f.GroupName}/{f.ChannelName}";
                if (f.Mean is { } vm && double.IsFinite(vm))
                { NormalModelStore.Observe(db, cfg.LearnNormalMinSamples, NormalModelStore.SourceWaveform, model, baseKey + "/vmean", vm, ts); Interlocked.Increment(ref _wUpd); }
                if (f.Std is { } vs && double.IsFinite(vs))
                { NormalModelStore.Observe(db, cfg.LearnNormalMinSamples, NormalModelStore.SourceWaveform, model, baseKey + "/vstd", vs, ts); Interlocked.Increment(ref _wUpd); }
                if (f.Max is { } vx && double.IsFinite(vx))
                { NormalModelStore.Observe(db, cfg.LearnNormalMinSamples, NormalModelStore.SourceWaveform, model, baseKey + "/vmax", vx, ts); Interlocked.Increment(ref _wUpd); }
            }
            MaybeFlush(db);
        }
        catch (Exception ex) { Interlocked.Increment(ref _wErr); Logger.Warning($"[正常态] 波形学习异常(已吞并): {ex.Message}"); }
    }

    public static void ObserveDeviceSample(Database db, AppConfig cfg, double cpu, double memPct, double diskFreeGb, string? ts = null)
    {
        if (!cfg.LearnNormalEnabled || db == null) return;
        try
        {
            var t = string.IsNullOrWhiteSpace(ts) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : TimeUtil.Normalize(ts);
            if (t.Length < 13) t = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var slot = TimeSlot.SlotOfHour(int.Parse(t.Substring(11, 2)));
            foreach (var (name, val) in new[] { ("cpu", cpu), ("mem", memPct), ("disk", diskFreeGb) })
            {
                if (!double.IsFinite(val)) continue;
                NormalModelStore.Observe(db, cfg.LearnNormalMinSamples, NormalModelStore.SourceDevice, "", $"{name}@{slot}", val, t);
                Interlocked.Increment(ref _dUpd);
            }
            MaybeFlush(db);
        }
        catch (Exception ex) { Interlocked.Increment(ref _dErr); Logger.Warning($"[正常态] 设备学习异常(已吞并): {ex.Message}"); }
    }

    /// <summary>计数达阈值时落库；提供显式 Flush 供 selftest/UI 用。</summary>
    private static void MaybeFlush(Database db)
    {
        if (Interlocked.Increment(ref _sinceFlush) % FlushEveryUpdates == 0) FlushHeartbeat(db);
    }

    public static string FlushHeartbeat(Database db)
    {
        lock (_hbLock)
        {
            var json = JsonSerializer.Serialize(new
            {
                measurement = new { updates = _mUpd, errors = _mErr },
                waveform = new { updates = _wUpd, errors = _wErr },
                device = new { updates = _dUpd, errors = _dErr },
                flushed_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            });
            try { db?.SetMeta(MetaHeartbeat, json); } catch { }
            return json;
        }
    }
}
