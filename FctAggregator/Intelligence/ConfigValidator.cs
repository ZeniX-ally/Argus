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
        return errs;
    }
}
