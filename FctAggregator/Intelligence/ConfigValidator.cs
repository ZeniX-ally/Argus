namespace FctAggregator;

public static class ConfigValidator
{
    public static List<string> Validate(AppConfig cfg)
    {
        var errs = new List<string>();
        if (string.IsNullOrWhiteSpace(cfg.ResultsRoot))
            errs.Add("results_root 为空");
        if (cfg.TodoScanDays < 1 || cfg.TodoScanDays > 365)
            errs.Add($"todo_scan_days 越界: {cfg.TodoScanDays}");
        if (cfg.DbMaintenanceHour < 0 || cfg.DbMaintenanceHour > 23)
            errs.Add($"db_maintenance_hour 越界: {cfg.DbMaintenanceHour}");
        if (cfg.DbStorageMonitorHours < 1 || cfg.DbStorageMonitorHours > 24)
            errs.Add($"db_storage_monitor_hours 越界: {cfg.DbStorageMonitorHours}");
        if (cfg.DbVacuumThresholdMb < 100 || cfg.DbVacuumThresholdMb > 50_000)
            errs.Add($"db_vacuum_threshold_mb 越界: {cfg.DbVacuumThresholdMb}");
        if (cfg.DbParseFailureRetentionDays < 7 || cfg.DbParseFailureRetentionDays > 365)
            errs.Add($"db_parse_failure_retention_days 越界: {cfg.DbParseFailureRetentionDays}");
        if (cfg.AnalyzeMeasureRetentionDays < LearnPipeline.MeasureRetentionMin ||
            cfg.AnalyzeMeasureRetentionDays > LearnPipeline.MeasureRetentionMax)
            errs.Add($"analyze_measure_retention_days 越界: {cfg.AnalyzeMeasureRetentionDays}（允许 {LearnPipeline.MeasureRetentionMin}～{LearnPipeline.MeasureRetentionMax}）");
        if (string.IsNullOrWhiteSpace(cfg.FctProgramSourceRoot))
            errs.Add("fct_program_source_root 为空");
        if (string.IsNullOrWhiteSpace(cfg.FctProgramBackupDir))
            errs.Add("fct_program_backup_dir 为空");
        if (cfg.FctProgramBackupKeep < 1 || cfg.FctProgramBackupKeep > 100)
            errs.Add($"fct_program_backup_keep 越界: {cfg.FctProgramBackupKeep}");
        return errs;
    }
}
