namespace FctAggregator;

/// <summary>正常态历史回填：按时间序把库内 PASS 测量 / TDMS / 设备采样喂给三学习器。水位幂等。</summary>
public static class LearnBackfill
{
    public const string MetaMeas = "learn_normal_bf_meas_rid";
    public const string MetaTdms = "learn_normal_bf_tdms_rid";
    public const string MetaDev = "learn_normal_bf_dev_id";

    public static int Run(Database db, AppConfig cfg, int days, CancellationToken ct = default)
    {
        if (db == null || cfg == null || !cfg.LearnNormalEnabled) return 0;
        days = Math.Clamp(days, 1, 3650);
        var from = DateTime.Now.Date.AddDays(-(days - 1)).ToString("yyyy-MM-dd");
        int processed = 0;
        try { processed += BackfillMeasurements(db, cfg, from, ct); }
        catch (Exception ex) { Logger.Warning($"[正常态] 测量回填异常(已吞并): {ex.Message}"); }
        try { processed += BackfillTdms(db, cfg, from, ct); }
        catch (Exception ex) { Logger.Warning($"[正常态] 波形回填异常(已吞并): {ex.Message}"); }
        try { processed += BackfillDevice(db, cfg, from, ct); }
        catch (Exception ex) { Logger.Warning($"[正常态] 设备回填异常(已吞并): {ex.Message}"); }
        NormalLearners.FlushHeartbeat(db);
        return processed;
    }

    private static long ReadWm(Database db, string key)
        => long.TryParse(db.GetMeta(key), out var n) && n >= 0 ? n : 0;

    /// <summary>推进回填水位（单调，只增不降）。实时喂样与回填共用同一水位，防止同一记录被喂两遍导致 n 双计。</summary>
    public static void AdvanceWatermark(Database db, string key, long id)
    {
        try
        {
            if (db == null || id <= 0) return;
            var cur = ReadWm(db, key);
            if (id > cur) db.SetMeta(key, id.ToString());
        }
        catch (Exception ex) { Logger.Warning($"[正常态] 水位推进失败: {ex.Message}"); }
    }

    private static int BackfillMeasurements(Database db, AppConfig cfg, string from, CancellationToken ct)
    {
        var rows = db.ListPassMeasurementsAfter(ReadWm(db, MetaMeas), from);
        if (rows.Count == 0) return 0;
        int n = 0, sinceSleep = 0;
        foreach (var grp in rows.GroupBy(r => r.RecordId))
        {
            ct.ThrowIfCancellationRequested();
            var first = grp.First();
            var rec = new TestRecord
            {
                Model = first.Model, Result = "PASS", BatchTimestamp = first.Ts,
                Measurements = grp.Select(g => new MeasurementRow { TestName = g.TestName, Value = g.Value }).ToList(),
            };
            NormalLearners.ObserveMeasurement(db, cfg, rec);
            db.SetMeta(MetaMeas, grp.Key.ToString());
            n++;
            if (++sinceSleep >= 200) { Thread.Sleep(100); sinceSleep = 0; }
        }
        return n;
    }

    private static int BackfillTdms(Database db, AppConfig cfg, string from, CancellationToken ct)
    {
        var rows = db.ListPassTdmsAfter(ReadWm(db, MetaTdms), from);
        if (rows.Count == 0) return 0;
        int n = 0, sinceSleep = 0;
        foreach (var grp in rows.GroupBy(r => r.RecordId))
        {
            ct.ThrowIfCancellationRequested();
            var first = grp.First();
            var rec = new TestRecord { Model = first.Model, Result = "PASS", BatchTimestamp = first.Ts };
            var feats = grp.Select(g => new TdmsFeatureAnalyzer.TdmsFeatureRow
            {
                GroupName = g.Group, ChannelName = g.Channel, Mean = g.Mean, Std = g.Std, Max = g.Max,
            }).ToList();
            NormalLearners.ObserveWaveform(db, cfg, rec, feats);
            AdvanceWatermark(db, MetaTdms, grp.Key);
            n++;
            if (++sinceSleep >= 200) { Thread.Sleep(100); sinceSleep = 0; }
        }
        return n;
    }

    private static int BackfillDevice(Database db, AppConfig cfg, string from, CancellationToken ct)
    {
        var rows = db.ListDeviceSamplesAfter(ReadWm(db, MetaDev), from);
        if (rows.Count == 0) return 0;
        int n = 0, sinceSleep = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            NormalLearners.ObserveDeviceSample(db, cfg, row.Cpu, row.Mem, row.Disk, row.Ts);
            db.SetMeta(MetaDev, row.Id.ToString());
            n++;
            if (++sinceSleep >= 200) { Thread.Sleep(100); sinceSleep = 0; }
        }
        return n;
    }
}
