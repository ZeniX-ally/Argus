namespace FctAggregator;

/// <summary>自学习 UI/CLI 编排：磁盘补采测量值 → 正常态入模 →（可选）现场回放。</summary>
public static class LearnPipeline
{
    public const int MeasureRetentionMin = 7;
    /// <summary>测量/TDMS 明细滚动保留上限（与回填窗口下拉一致，避免「回填 180 天、清理只留 30 天」）。</summary>
    public const int MeasureRetentionMax = 365;

    /// <summary>
    /// 库维护与入库裁剪用：配置保留期与（自学习/分析开启时的）回填窗口下限取较大值，上限 <see cref="MeasureRetentionMax"/>。
    /// 实现 v3.35.2 意图且避免 v3.36.10 前「跟全库最早 PASS 无限保留」撑爆库。
    /// </summary>
    public static int EffectiveMeasureRetentionDays(AppConfig cfg, Database? db, int backfillWindowIndex = 0)
    {
        int r = Math.Clamp(cfg.AnalyzeMeasureRetentionDays, MeasureRetentionMin, MeasureRetentionMax);
        if (!cfg.LearnNormalEnabled && !cfg.AnalyzeCollectPass) return r;
        int window;
        if (backfillWindowIndex > 0)
            window = Math.Clamp(ResolveBackfillDays(db!, backfillWindowIndex), MeasureRetentionMin, MeasureRetentionMax);
        else if (db != null)
            window = Math.Clamp(ResolveBackfillDays(db, 0), MeasureRetentionMin, MeasureRetentionMax);
        else
            return r;
        return Math.Max(r, window);
    }

    // 审计修复：EffectiveMeasureRetentionDays 内部会跑 GetEarliestPassRecordYmd()——那是一条对表达式求
    // MIN 的全表扫描（走不了索引），而 Processor.ParseAndClassify 是「每个 XML 文件」都会调它一次。
    // 实测 20 万行（10 万 PASS）单次 41ms，首次历史扫描因此呈 O(N²)（十万文件量级多花十几分钟）。
    // 这里按库路径缓存，且**只允许单调加宽**：更宽的保留期意味着「多留」而非「多删」，任何时刻都不会
    // 误删本应保留的测量值，因此无需精确失效，只需定期重算。
    private static readonly object _retLock = new();
    private static string? _retKey;
    private static int _retValue;
    private static long _retStamp;

    /// <summary>带缓存的有效保留期（默认 5 分钟重算一次；期间返回值单调不减）。</summary>
    public static int EffectiveMeasureRetentionDaysCached(AppConfig cfg, Database? db)
    {
        if (db == null) return EffectiveMeasureRetentionDays(cfg, db);
        lock (_retLock)
        {
            var now = Environment.TickCount64;
            if (_retKey == db.DbPath && now - _retStamp < 300_000) return _retValue;
            var v = EffectiveMeasureRetentionDays(cfg, db);
            if (_retKey == db.DbPath) v = Math.Max(v, _retValue); // 单调加宽，不回退
            _retKey = db.DbPath;
            _retValue = v;
            _retStamp = now;
            return v;
        }
    }

    /// <summary>窗口下拉：0=自动，1=30，2=90，3=180，4=365。</summary>
    public static int ResolveBackfillDays(Database db, int windowIndex)
    {
        if (windowIndex > 0)
            return windowIndex switch { 1 => 30, 2 => 90, 3 => 180, _ => 365 };
        var earliest = db.GetEarliestPassRecordYmd();
        if (string.IsNullOrEmpty(earliest) || earliest.Length < 10) return 90;
        if (!DateTime.TryParseExact(earliest, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var ed))
            return 90;
        int days = (int)(DateTime.Now.Date - ed).TotalDays + 1;
        return Math.Clamp(days, 30, 3650);
    }

    public sealed class FullBackfillResult
    {
        public int DaysUsed;
        public BackfillReport Measurement = new();
        public int LearnProcessed;
        public bool MeasurementSkipped;
        public string? MeasurementSkipReason;

        public string FormatSummary()
        {
            var parts = new List<string> { $"窗口 {DaysUsed} 天" };
            if (MeasurementSkipped)
                parts.Add(MeasurementSkipReason ?? "未补采测量");
            else
                parts.Add($"扫描 {Measurement.ScannedFiles} 文件，补测量 {Measurement.InsertedMeasurements} 行");
            if (Measurement.SnFixed > 0) parts.Add($"SN {Measurement.SnFixed}");
            parts.Add($"入模 {LearnProcessed} 条 PASS");
            return string.Join("；", parts);
        }
    }

    /// <summary>一步完成：SN 补采 → 磁盘补测量/TDMS → 正常态历史入模。</summary>
    public static FullBackfillResult RunFullBackfill(
        Engine engine, int windowIndex, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var cfg = engine.Config;
        var db = engine.Db;
        var days = ResolveBackfillDays(db, windowIndex);
        var result = new FullBackfillResult { DaysUsed = days };
        var processor = new Processor(cfg, engine.ResolvedStationId, Parsing.ParserRegistry.Instance, db);

        progress?.Report("SN 补采…");
        int snFixed = BackfillTool.BackfillMissingSn(db, processor, ct);

        bool canMeas = cfg.AnalyzeCollectPass ||
                       (cfg.AnalyzeTdmsEnabled && !string.IsNullOrWhiteSpace(cfg.TdmsRoot));
        if (canMeas)
        {
            progress?.Report($"补采测量值（{days} 天）…");
            result.Measurement = BackfillTool.RunMeasurementBackfill(db, cfg, processor, days,
                msg => progress?.Report(msg), ct);
        }
        else
        {
            result.MeasurementSkipped = true;
            result.MeasurementSkipReason = "analyze_collect_pass 关且 TDMS 未配";
        }
        result.Measurement.SnFixed = snFixed;

        if (cfg.LearnNormalEnabled)
        {
            progress?.Report($"训练正常态（{days} 天）…");
            result.LearnProcessed = LearnBackfill.Run(db, cfg, days, ct);
        }

        return result;
    }
}
