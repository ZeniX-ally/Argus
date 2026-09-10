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
            AnalysisEngine.RunOnce(_db, _cfg);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[库维护] 分析引擎执行失败（不影响运行）: {ex.Message}");
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
                var retain = Math.Clamp(_cfg.AnalyzeMeasureRetentionDays, 7, 90);
                _db.PurgeOldMeasurements(retain);
                _db.PurgeOldFailItems(Math.Clamp(_cfg.AnalyzeFailItemRetentionDays, 7, 180));
                _db.PurgeOldTdmsFeatures(retain);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[库维护] 存储优化失败: {ex.Message}");
        }
        try
        {
            var bak = _db.BackupDaily();
            if (bak != null) Logger.Info($"[库维护] 每日备份完成: {Path.GetFileName(bak)}（保留 {Database.BackupKeepDays} 份）");
        }
        catch (Exception ex)
        {
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
    }
}
