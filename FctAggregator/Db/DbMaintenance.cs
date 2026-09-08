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
            for (int i = 0; i < 60 && !_stopping; i++)
                try { Thread.Sleep(1000); } catch { }
        }
    }

    public void RunNow()
    {
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
            var retain = _cfg.LearnResourceRetentionDays;
            var purged = _db.PurgeOldLocalDeviceSamples(retain);
            if (purged > 0) Logger.Info($"[库维护] 本机设备采样清理: 删除 {purged} 条（保留 {retain} 天）");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[库维护] 本机设备采样清理失败: {ex.Message}");
        }
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
            var retain = _cfg.AnalyzeMeasureRetentionDays;
            var purged = _db.PurgeOldMeasurements(retain);
            if (purged > 0) Logger.Info($"[库维护] 测量值清理: 删除 {purged} 条（保留 {retain} 天）");
            var retainFi = _cfg.AnalyzeFailItemRetentionDays;
            var purgedFi = _db.PurgeOldFailItems(retainFi);
            if (purgedFi > 0) Logger.Info($"[库维护] 失败项明细清理: 删除 {purgedFi} 条（保留 {retainFi} 天）");
            var purgedTf = _db.PurgeOldTdmsFeatures(retain);
            if (purgedTf > 0) Logger.Info($"[库维护] TDMS 特征清理: 删除 {purgedTf} 条（保留 {retain} 天）");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[库维护] 测量值清理失败: {ex.Message}");
        }
    }
}
