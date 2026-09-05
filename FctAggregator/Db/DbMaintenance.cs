namespace FctAggregator;

public sealed class DbMaintenance
{
    private readonly AggDatabase _db;
    private readonly int _hour;
    private readonly long _vacuumThresholdBytes;
    private Thread? _thread;
    private volatile bool _stopping;
    private DateTime _lastRunDate = DateTime.MinValue;

    private DbMaintenance(AggDatabase db, int hour, long vacuumThresholdBytes)
    {
        _db = db;
        _hour = hour;
        _vacuumThresholdBytes = vacuumThresholdBytes;
    }

    public static DbMaintenance StartFor(AppConfig cfg, AggDatabase db)
    {
        var m = new DbMaintenance(db, cfg.DbMaintenanceHour, (long)cfg.DbVacuumThresholdMb * 1024 * 1024);
        m.Start();
        return m;
    }

    public void Start()
    {
        if (_thread != null) return;
        _stopping = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "agg-db-maintenance" };
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
            if (now.Hour == _hour && now.Date != _lastRunDate)
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
            var summary = _db.RunMaintenance(_vacuumThresholdBytes);
            Logger.Info($"[库维护] {Path.GetFileName(_db.DbPath)}: {summary}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[库维护] {_db.DbPath} 维护失败（不影响运行）: {ex.Message}");
        }
        try
        {
            var bak = _db.BackupDaily();
            if (bak != null) Logger.Info($"[库维护] 每日备份完成: {Path.GetFileName(bak)}（保留 {AggDatabase.BackupKeepDays} 份）");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[库维护] {_db.DbPath} 每日备份失败（不影响运行）: {ex.Message}");
        }
        try
        {
            var retain = AppConfig.Instance.DeviceSamplesRetainDays;
            var purged = _db.PurgeOldDeviceSamples(retain);
            if (purged > 0) Logger.Info($"[库维护] 设备采样清理: 删除 {purged} 条（保留 {retain} 天）");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[库维护] 设备采样清理失败: {ex.Message}");
        }
        try
        {
            PredictAccuracyReconciler.RunOnce(_db, AppConfig.Instance);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[库维护] 预测对账失败: {ex.Message}");
        }
        try
        {
            var retain = AppConfig.Instance.PredictAccuracyRetainDays;
            var purged = _db.PurgeOldPredictAccuracy(retain);
            if (purged > 0) Logger.Info($"[库维护] 预测对账清理: 删除 {purged} 条（保留 {retain} 天）");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[库维护] 预测对账清理失败: {ex.Message}");
        }
        try
        {
            var localDb = Database.Current;
            if (localDb != null)
            {
                var retain = AppConfig.Instance.LearnResourceRetentionDays;
                var purged = localDb.PurgeOldLocalDeviceSamples(retain);
                if (purged > 0) Logger.Info($"[库维护] 本机设备采样清理: 删除 {purged} 条（保留 {retain} 天）");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[库维护] 本机设备采样清理失败: {ex.Message}");
        }
        try
        {
            var localDb = Database.Current;
            if (localDb != null) LearningEngine.RunOnce(localDb, AppConfig.Instance);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[库维护] 自学习引擎执行失败（不影响运行）: {ex.Message}");
        }
    }
}
