namespace FctAggregator;

/// <summary>本机库每日维护：每日到达 db_maintenance_hour 后执行（含滞后补跑）。
/// 原实现由 MeshNode 承载并同时维护聚合库（mesh_agg.db）；聚合/互联功能移除后，
/// 仅保留本机 Database 的自学习/深度分析/滚动清理/每日备份部分。</summary>
public sealed class DbMaintenance
{
    private readonly Database _db;
    private readonly AppConfig _cfg;
    private readonly int _hour;
    private Thread? _thread;
    private volatile bool _stopping;
    private DateTime _lastRunDate = DateTime.MinValue;
    private DateTime _lastLightRunUtc = DateTime.MinValue;
    private string? _lastBackupName; // 每日备份结果（摘要卡用）

    /// <summary>重试队列积压条数提供者（Engine 注入；未注入按 0 计）。</summary>
    public Func<int>? RetryQueueDepthProvider { get; set; }
    /// <summary>已解析的机台号提供者（Engine 注入；未注入回落 config.StationId）。</summary>
    public Func<string>? StationIdProvider { get; set; }

    private DbMaintenance(Database db, AppConfig cfg, int hour)
    {
        _db = db;
        _cfg = cfg;
        _hour = hour;
    }

    public static DbMaintenance StartFor(AppConfig cfg, Database db)
    {
        var m = new DbMaintenance(db, cfg, cfg.DbMaintenanceHour);
        m.Start();
        return m;
    }

    public void Start()
    {
        if (_thread != null) return;
        _stopping = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "local-db-maintenance" };
        _thread.Start();
    }

    public void Stop()
    {
        _stopping = true;
        try { _thread?.Join(2000); } catch { }
        _thread = null;
    }

    private void Loop()
    {
        while (!_stopping)
        {
            var now = DateTime.Now;
            // 精确整点匹配曾导致错过维护小时当天全链跳过且无补跑——
            // 改为到达维护小时后当日首个 tick 执行（滞后补跑）
            if (now.Hour >= _hour && now.Date != _lastRunDate)
            {
                RunNow();
                _lastRunDate = now.Date;
            }
            if (_cfg.DbStorageAutoOptimize)
            {
                var intervalH = Math.Clamp(_cfg.DbStorageMonitorHours, 1, 24);
                if ((DateTime.UtcNow - _lastLightRunUtc).TotalHours >= intervalH)
                {
                    try { StorageOptimizer.Run(_db, _cfg, StorageOptimizeMode.Light); }
                    catch (Exception ex) { Logger.Warning($"[库维护] 存储轻量巡检失败: {ex.Message}"); }
                    _lastLightRunUtc = DateTime.UtcNow;
                }
            }
            for (int i = 0; i < 60 && !_stopping; i++)
                try { Thread.Sleep(1000); } catch { }
        }
    }

    public void RunNow()
    {
        try
        {
            LearningEngine.RunOnce(_db, _cfg);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[库维护] 自学习引擎执行失败（不影响运行）: {ex.Message}");
        }
        try
        {
            if (_cfg.DbStorageAutoOptimize)
            {
                var rep = StorageOptimizer.Run(_db, _cfg, StorageOptimizeMode.Deep);
                Logger.Info($"[库维护] 存储深度优化: {rep.Summary}");
            }
            else
            {
                // 审计修复：关掉自动优化（现场为规避 VACUUM 卡顿）不等于放弃保留期——原来这里只清三类
                // 明细，parse_failure_log / db_slow_log / device_samples_local 的滚动策略静默失效、无界增长。
                var retain = LearnPipeline.EffectiveMeasureRetentionDays(_cfg, _db);
                _db.PurgeOldMeasurements(retain);
                _db.PurgeOldFailItems(Math.Clamp(_cfg.AnalyzeFailItemRetentionDays, 7, 180));
                _db.PurgeOldTdmsFeatures(retain);
                _db.PurgeOldLocalDeviceSamples(Math.Clamp(_cfg.LearnResourceRetentionDays, 7, 90));
                _db.PurgeOldParseFailures(_cfg.DbParseFailureRetentionDays);
                _db.PurgeOldSlowLogs(14);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[库维护] 存储优化失败: {ex.Message}");
        }
        try
        {
            var bak = _db.BackupDaily();
            _lastBackupName = bak;
            if (bak != null) Logger.Info($"[库维护] 每日备份完成: {Path.GetFileName(bak)}（保留 {Database.BackupKeepDays} 份）");
        }
        catch (Exception ex)
        {
            _lastBackupName = null;
            Logger.Warning($"[库维护] 本机库每日备份失败（不影响运行）: {ex.Message}");
        }
        try
        {
            if (FctProgramBackup.ShouldRunScheduled(_db, _cfg))
            {
                var sid = string.IsNullOrEmpty(_cfg.StationId) ? "AUTO" : _cfg.StationId;
                var rep = FctProgramBackup.Run(_db, _cfg, sid, FctProgramBackup.TriggerScheduled);
                if (rep.Ok)
                    Logger.Info($"[库维护] FTS 测试程序备份: {rep.Message}");
                else
                    Logger.Warning($"[库维护] FTS 测试程序备份: {rep.Message}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[库维护] FTS 测试程序备份失败: {ex.Message}");
        }

        // 运维告警（存储健康 / 待办超期）与每日摘要：均按日期去重，重启补跑不会重复推
        try
        {
            OpsAlertMonitor.RunStorageCheck(_db, _cfg);
            OpsAlertMonitor.RunTodoOverdueCheck(_db, _cfg);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[运维告警] 评估失败（不影响运行）: {ex.Message}");
        }

        // 每日摘要卡：复用上面各步已算出的结果汇总成一张例行卡（不额外计算，不产生噪音）
        try
        {
            if (_cfg.FeishuDailySummaryEnabled) SendDailySummary();
        }
        catch (Exception ex)
        {
            Logger.Warning($"[每日摘要] 汇总失败（不影响运行）: {ex.Message}");
        }
    }

    /// <summary>汇总「最近一个完整日」的运行摘要并推飞书。</summary>
    private void SendDailySummary()
    {
        var now = DateTime.Now;
        var day = CollectHealthMonitor.SummaryDayOf(now);
        var dayKey = day.ToString("yyyyMMdd");

        // 按日期去重：RunNow 在每个进程启动时都会补跑一次（_lastRunDate 只在内存里），
        // 不去重的话重启几次就推几张摘要卡。落 app_meta 保证跨重启只推一次。
        if (_db.GetMeta(CollectHealthMonitor.MetaLastSummaryDay) == dayKey)
        {
            Logger.Info($"[每日摘要] {day:yyyy-MM-dd} 摘要已推送过，跳过");
            return;
        }

        var stationId = (StationIdProvider?.Invoke() ?? _cfg.StationId) ?? "";
        var stationFilter = string.IsNullOrEmpty(stationId) ? "" : stationId;

        var stats = _db.FetchDailyStats(stationFilter, dayKey);
        var top = _db.FetchDailyTopFails(stationFilter, dayKey, 5);

        var health = StorageOptimizer.Evaluate(_db, _cfg);
        var since = CollectHealthMonitor.WindowStart(now, _cfg.FeishuCollectAlertWindowMin);
        var abnormal = _db.CountAbnormalParseFailures(since, string.IsNullOrEmpty(stationId) ? null : stationId);

        var payload = CollectHealthMonitor.BuildDailySummary(
            stationId, day, stats, top, health.Score, health.FileBytes, _lastBackupName,
            RetryQueueDepthProvider?.Invoke() ?? 0, abnormal, _cfg.FeishuCollectAlertWindowMin);

        CollectHealthMonitor.SendDailySummary(_cfg, payload);
        _db.SetMeta(CollectHealthMonitor.MetaLastSummaryDay, dayKey);
        Logger.Info($"[每日摘要] {payload.Date} 摘要已推送（总数 {payload.Total}，良率 {payload.YieldPct:F2}%，FAIL {payload.Fail}，库健康分 {payload.StorageScore}）");
    }
}
