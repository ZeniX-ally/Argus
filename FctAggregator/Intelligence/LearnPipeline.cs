namespace FctAggregator;

/// <summary>自学习 UI/CLI 编排：磁盘补采测量值 → 正常态入模 →（可选）现场回放。</summary>
public static class LearnPipeline
{
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

        Task.Run(() =>
        {
            try { AnalysisEngine.RunOnce(db, cfg); }
            catch (Exception ex) { Logger.Warning($"[正常态] 回填后分析刷新失败: {ex.Message}"); }
        });

        return result;
    }
}
