namespace FctAggregator;

/// <summary>
/// 副本库每日低峰维护（v3.5.3，P0-5）：到 <see cref="AppConfig.DbMaintenanceHour"/> 点执行
/// wal_checkpoint(TRUNCATE)（收缩 WAL），库超过 <see cref="AppConfig.DbVacuumThresholdMb"/> 再 VACUUM。
///
/// 背景：WAL 模式下 checkpoint 由 SQLite 自动尝试，但读连接不断时 WAL 可能长期不收缩；
/// 无归档/清理前先用这条线兜住 db 与 -wal 文件体积，防止长期运行后查询退化与磁盘占满。
/// 默认 03:00 执行；同一天内只跑一次（失败次日再试）。每节点只维护自己的副本库 ——
/// 本地机台库已有迁移前备份机制，动它需要更谨慎的窗口协调，暂不纳入。
/// </summary>
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

    /// <summary>按配置启动每日维护线程（节点启动时调用一次；重复调用无副作用）。</summary>
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
                _lastRunDate = now.Date;   // 无论成败都标记当天已尝试，避免失败风暴；明天会再来
            }
            // 60 秒粒度轮询整点，Stop 时最多等一分钟
            for (int i = 0; i < 60 && !_stopping; i++)
                try { Thread.Sleep(1000); } catch { }
        }
    }

    /// <summary>立即执行一轮 checkpoint(+VACUUM)，日志记录结果。供自检/运维手动触发。</summary>
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
        // v3.10.0 P1-2：每日备份随维护线执行（副本库容灾恢复点）
        try
        {
            var bak = _db.BackupDaily();
            if (bak != null) Logger.Info($"[库维护] 每日备份完成: {Path.GetFileName(bak)}（保留 {AggDatabase.BackupKeepDays} 份）");
        }
        catch (Exception ex)
        {
            Logger.Warning($"[库维护] {_db.DbPath} 每日备份失败（不影响运行）: {ex.Message}");
        }
        // v3.14.0 P6：设备采样滚动保留（默认 7 天），每日清理一次
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
        // v3.20.0 规格 02：预测准确率对账（每日凌晨顺带）
        try
        {
            PredictAccuracyReconciler.RunOnce(_db, AppConfig.Instance);
        }
        catch (Exception ex)
        {
            Logger.Warning($"[库维护] 预测对账失败: {ex.Message}");
        }
        // v3.20.0 规格 02：滚动清理 predict_accuracy_log
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
        // v3.24.0 规格 05：机台端本地设备自采样滚动清理 (默认 14 天)
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
        // v3.24.0 规格 05 S3/S4：机台自学习每日窗口（基线评估/章节群挂/权重校准）。
        // 全部开关关闭时为 no-op；逐段内部独立 try/catch，这里只兜总异常。
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
