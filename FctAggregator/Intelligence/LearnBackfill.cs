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
        var wm = ReadWm(db, MetaMeas);
        var rows = db.ListPassMeasurementsAfter(wm, from);
        if (rows.Count == 0)
        {
            // C2：水位按 record_id、窗口按天——二次回填（换窗口/重建模型）时旧记录恒被水位跳过。
            // 不静默返回 0：明示原因，避免现场以为「回填没用」。
            if (wm > 0)
                Logger.Info($"[正常态] 测量回填 0 条：水位 {wm} 已覆盖窗口起点 {from}（重建模型需先清 normal_models）");
            return 0;
        }
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
            // C1：必须走 AdvanceWatermark（单调）——实时路径 Engine.AfterIngest 也推进同一水位，
            // 直接 SetMeta 会在回填与实时并发时把水位写回更小值 → 重复入模、n 双计、偏离误报。
            AdvanceWatermark(db, MetaMeas, grp.Key);
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
            // C1：同测量水位，单调推进（原直接 SetMeta 会写回更小值）
            AdvanceWatermark(db, MetaDev, row.Id);
            n++;
            if (++sinceSleep >= 200) { Thread.Sleep(100); sinceSleep = 0; }
        }
        return n;
    }
}
