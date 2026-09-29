namespace FctAggregator;

/// <summary>存储分层：核心记录 / 可滚动明细 / 学习模型 / 设备采样 / 运行日志。</summary>
public sealed class StorageTier
{
    public string Table { get; init; } = "";
    public string Category { get; init; } = "";
    public long Rows { get; init; }
    /// <summary>0 = 不按天滚动删除。</summary>
    public int RetentionDays { get; init; }
    public string Policy { get; init; } = "";
}

public sealed class StorageHealthReport
{
    public int Score;
    public long FileBytes;
    public long WalBytes;
    public long PageCount;
    public long FreePages;
    public double BloatRatio;
    public bool SlimSchema;
    public List<StorageTier> Tiers = new();
    public List<string> Issues = new();
    public List<string> PlannedActions = new();

    public string FormatHeadline()
    {
        var size = FileBytes >= 1_000_000_000 ? $"{FileBytes / 1_000_000_000.0:F2} GB" : $"{FileBytes / 1_000_000.0:F1} MB";
        return $"健康分 {Score}/100  库 {size}" + (WalBytes > 0 ? $"  WAL {WalBytes / 1_000_000.0:F1} MB" : "") +
               (SlimSchema ? "  瘦表" : "  冗余列待压缩");
    }

    public IEnumerable<string> FormatLines()
    {
        yield return FormatHeadline();
        if (PageCount > 0)
            yield return $"  空洞率 {BloatRatio * 100:F1}%（空闲页 {FreePages:N0}/{PageCount:N0}）";
        foreach (var t in Tiers.Where(x => x.Rows > 0))
            yield return $"  [{t.Category}] {t.Table}: {t.Rows:N0} 行  {t.Policy}";
        foreach (var i in Issues)
            yield return $"  ! {i}";
        foreach (var a in PlannedActions)
            yield return $"  → {a}";
    }
}

public enum StorageOptimizeMode { Light, Deep }

public sealed class StorageOptimizeResult
{
    public StorageOptimizeMode Mode;
    public int ScoreBefore;
    public int ScoreAfter;
    public int PurgedParseFailures;
    public int PurgedSlowLogs;
    public int PurgedDeviceSamples;
    public int PurgedMeasurements;
    public int PurgedFailItems;
    public int PurgedTdms;
    public bool Rebuilt;
    public bool Vacuumed;
    public long BytesBefore;
    public long BytesAfter;
    public string Summary = "";

    public IEnumerable<string> FormatLines()
    {
        yield return $"[{Mode}] {Summary}";
        yield return $"  健康分 {ScoreBefore} → {ScoreAfter}";
        if (PurgedParseFailures + PurgedSlowLogs + PurgedDeviceSamples + PurgedMeasurements + PurgedFailItems + PurgedTdms > 0)
            yield return $"  清理: 解析失败 {PurgedParseFailures}  慢查询 {PurgedSlowLogs}  采样 {PurgedDeviceSamples}  测量 {PurgedMeasurements}  失败项 {PurgedFailItems}  TDMS {PurgedTdms}";
        if (Rebuilt) yield return "  已重建瘦表（去掉路径冗余列）";
        if (Vacuumed)
            yield return $"  VACUUM: {ByteUtil.MbGb(BytesBefore)} → {ByteUtil.MbGb(BytesAfter)}";
    }
}

/// <summary>库容量监控与滚动优化：分类盘点 → 健康评分 → 定时轻量维护 / 每日深度压缩。</summary>
public static class StorageOptimizer
{
    public const string MetaLastRun = "storage_optimizer_last_run";
    public const string MetaLastScore = "storage_optimizer_last_score";

    public static StorageHealthReport Evaluate(Database db, AppConfig cfg)
    {
        var st = db.GetStorageStats();
        var pages = db.GetPageStats();
        var report = new StorageHealthReport
        {
            FileBytes = st.FileBytes,
            WalBytes = st.WalBytes,
            SlimSchema = st.SlimSchema,
            PageCount = pages.PageCount,
            FreePages = pages.FreePages,
            BloatRatio = pages.BloatRatio,
            Tiers = BuildTiers(db, cfg),
        };
        ScoreReport(report, st, cfg);
        report.PlannedActions = PlanActions(report, cfg);
        return report;
    }

    public static StorageOptimizeResult Run(Database db, AppConfig cfg, StorageOptimizeMode mode)
    {
        var before = Evaluate(db, cfg);
        var result = new StorageOptimizeResult
        {
            Mode = mode,
            ScoreBefore = before.Score,
            BytesBefore = before.FileBytes,
        };

        if (mode == StorageOptimizeMode.Light)
        {
            db.CheckpointForCopy();
            result.PurgedParseFailures = db.PurgeOldParseFailures(cfg.DbParseFailureRetentionDays);
            result.PurgedSlowLogs = db.PurgeOldSlowLogs(14);
            var afterLight = Evaluate(db, cfg);
            result.ScoreAfter = afterLight.Score;
            result.BytesAfter = afterLight.FileBytes;
            result.Summary = BuildLightSummary(result, afterLight);
            PersistMeta(db, result);
            if (afterLight.Score < 60 || afterLight.Issues.Count > 0)
                Logger.Warning($"[存储优化] {result.Summary}");
            else if (result.PurgedParseFailures + result.PurgedSlowLogs > 0)
                Logger.Info($"[存储优化] {result.Summary}");
            return result;
        }

        // Deep：滚动清理 → 瘦表重建 → 按需 VACUUM → 备份前已压缩
        var retainM = LearnPipeline.EffectiveMeasureRetentionDays(cfg, db);
        var retainFi = Math.Clamp(cfg.AnalyzeFailItemRetentionDays, 7, 180);
        var retainDev = Math.Clamp(cfg.LearnResourceRetentionDays, 7, 90);

        result.PurgedDeviceSamples = db.PurgeOldLocalDeviceSamples(retainDev);
        result.PurgedParseFailures = db.PurgeOldParseFailures(cfg.DbParseFailureRetentionDays);
        result.PurgedSlowLogs = db.PurgeOldSlowLogs(14);
        result.PurgedMeasurements = db.PurgeOldMeasurements(retainM);
        result.PurgedFailItems = db.PurgeOldFailItems(retainFi);
        result.PurgedTdms = db.PurgeOldTdmsFeatures(retainM);

        var purgedTotal = result.PurgedMeasurements + result.PurgedFailItems + result.PurgedTdms
                          + result.PurgedParseFailures + result.PurgedSlowLogs + result.PurgedDeviceSamples;

        if (!before.SlimSchema)
        {
            var rep = db.CompactStorage(retainM, vacuum: false, failItemRetentionDays: retainFi);
            result.Rebuilt = rep.Rebuilt;
            result.BytesBefore = rep.BytesBefore > 0 ? rep.BytesBefore : result.BytesBefore;
        }

        var afterPurge = Evaluate(db, cfg);
        var thresholdMb = Math.Clamp(cfg.DbVacuumThresholdMb, 100, 50_000);
        bool needVacuum = !before.SlimSchema
                          || purgedTotal >= 1000
                          || afterPurge.BloatRatio >= 0.15
                          || afterPurge.FileBytes >= thresholdMb * 1_000_000L;

        if (needVacuum && cfg.DbStorageAutoOptimize)
        {
            Logger.Info("[存储优化] 开始 VACUUM 回收磁盘…");
            db.Vacuum();
            result.Vacuumed = true;
        }

        var after = Evaluate(db, cfg);
        result.ScoreAfter = after.Score;
        try { if (File.Exists(db.DbPath)) result.BytesAfter = new FileInfo(db.DbPath).Length; }
        catch { result.BytesAfter = after.FileBytes; }

        result.Summary = $"深度优化完成，删除 {purgedTotal:N0} 行过期明细";
        PersistMeta(db, result);
        Logger.Info($"[存储优化] {result.Summary} | 健康分 {result.ScoreBefore}→{result.ScoreAfter} | {ByteUtil.MbGb(result.BytesBefore)}→{ByteUtil.MbGb(result.BytesAfter)}");
        return result;
    }

    private static void PersistMeta(Database db, StorageOptimizeResult result)
    {
        try
        {
            db.SetMeta(MetaLastRun, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            db.SetMeta(MetaLastScore, result.ScoreAfter.ToString());
        }
        catch { }
    }

    private static string BuildLightSummary(StorageOptimizeResult r, StorageHealthReport after)
    {
        var parts = new List<string> { "轻量巡检" };
        if (r.PurgedParseFailures > 0) parts.Add($"解析失败 -{r.PurgedParseFailures}");
        if (r.PurgedSlowLogs > 0) parts.Add($"慢查询 -{r.PurgedSlowLogs}");
        parts.Add($"健康分 {r.ScoreBefore}→{after.Score}");
        if (after.Issues.Count > 0) parts.Add($"待处理 {after.Issues.Count} 项");
        return string.Join("，", parts);
    }

    private static List<StorageTier> BuildTiers(Database db, AppConfig cfg)
    {
        var inv = db.GetStorageInventory();
        int retainM = LearnPipeline.EffectiveMeasureRetentionDays(cfg, db);
        int retainFi = Math.Clamp(cfg.AnalyzeFailItemRetentionDays, 7, 180);
        int retainDev = Math.Clamp(cfg.LearnResourceRetentionDays, 7, 90);
        int retainPf = Math.Clamp(cfg.DbParseFailureRetentionDays, 7, 180);

        return new List<StorageTier>
        {
            Tier("test_records", "核心", inv.TestRecords, 0, "良率/KPI 主记录，长期保留"),
            Tier("fail_items", "明细", inv.FailItems, retainFi, $"失败测项明细，滚动 {retainFi} 天"),
            Tier("test_measurements", "明细", inv.Measurements, retainM, $"PASS 测量值，滚动 {retainM} 天"),
            Tier("tdms_features", "明细", inv.TdmsFeatures, retainM, $"TDMS 通道特征，滚动 {retainM} 天"),
            Tier("normal_models", "学习", inv.NormalModels, 0, "正常态模型（已聚合，不需原始行）"),
            Tier("deviation_events", "学习", inv.DeviationEvents, 0, "偏离事件流水"),
            Tier("device_samples_local", "采样", inv.DeviceSamples, retainDev, $"本机 CPU/内存采样，滚动 {retainDev} 天"),
            Tier("parse_failure_log", "日志", inv.ParseFailures, retainPf, $"解析/跳过留痕，滚动 {retainPf} 天"),
            Tier("db_slow_log", "日志", inv.SlowLogs, 14, "慢 SQL 留痕，滚动 14 天"),
        };

        static StorageTier Tier(string table, string cat, long rows, int days, string policy) =>
            new() { Table = table, Category = cat, Rows = rows, RetentionDays = days, Policy = policy };
    }

    private static void ScoreReport(StorageHealthReport r, StorageStats st, AppConfig cfg)
    {
        int score = 100;
        if (!st.SlimSchema)
        {
            score -= 25;
            r.Issues.Add("明细表仍含 xml_path 等冗余列，占用约为瘦表的 2～5 倍");
        }
        if (st.FileBytes >= 5_000_000_000L)
        {
            score -= 25;
            r.Issues.Add("库文件超过 5GB，建议立即深度压缩");
        }
        else if (st.FileBytes >= 2_000_000_000L)
        {
            score -= 15;
            r.Issues.Add("库文件超过 2GB，建议安排 VACUUM");
        }
        else if (st.FileBytes >= 1_000_000_000L)
        {
            score -= 8;
            r.Issues.Add("库文件超过 1GB，关注明细保留天数");
        }

        if (st.WalBytes >= 200_000_000L)
        {
            score -= 10;
            r.Issues.Add("WAL 过大，将自动 checkpoint 合并");
        }

        if (r.BloatRatio >= 0.30)
        {
            score -= 15;
            r.Issues.Add($"删除数据后空洞率 {r.BloatRatio * 100:F0}%，需要 VACUUM 才能真正缩小文件");
        }
        else if (r.BloatRatio >= 0.15)
        {
            score -= 8;
            r.Issues.Add($"空洞率 {r.BloatRatio * 100:F0}%，建议 VACUUM");
        }

        if (st.TestRecords > 0 && st.Measurements > st.TestRecords * 800)
        {
            score -= 10;
            r.Issues.Add($"测量行/主记录比 {st.Measurements / (double)st.TestRecords:F0}:1 偏高，检查 analyze_max_tests_per_file 或保留天数");
        }

        if (st.TestRecords > 0 && st.Measurements > 0 && st.Measurements / (double)st.TestRecords > cfg.AnalyzeMaxTestsPerFile * 0.9)
        {
            score -= 5;
            r.Issues.Add("测量采集接近单文件上限，明细表增长较快");
        }

        r.Score = Math.Clamp(score, 0, 100);
    }

    private static List<string> PlanActions(StorageHealthReport r, AppConfig cfg)
    {
        var actions = new List<string>();
        if (!r.SlimSchema) actions.Add("重建瘦表（去掉路径/机台/型号/SN 冗余列）");
        if (r.Tiers.Any(t => t.RetentionDays > 0 && t.Rows > 0))
            actions.Add("按保留策略删除过期明细/日志/采样");
        var thresholdMb = Math.Clamp(cfg.DbVacuumThresholdMb, 100, 50_000);
        if (r.BloatRatio >= 0.15 || r.FileBytes >= thresholdMb * 1_000_000L)
            actions.Add("VACUUM 回收磁盘空洞");
        if (actions.Count == 0) actions.Add("当前结构健康，仅做 WAL checkpoint");
        return actions;
    }
}
