namespace FctAggregator;

/// <summary>域3 配置智能化 — 校验（v3.19.0）：peers/路径/阈值非法自动提示。</summary>
public static class ConfigValidator
{
    public static List<string> Validate(AppConfig cfg)
    {
        var errs = new List<string>();
        if (cfg.MeshPort < 1 || cfg.MeshPort > 65535)
            errs.Add($"mesh_port 非法: {cfg.MeshPort} (1-65535)");
        if (cfg.AggHttpPort < 1 || cfg.AggHttpPort > 65535)
            errs.Add($"agg_http_port 非法: {cfg.AggHttpPort}");
        if (string.IsNullOrWhiteSpace(cfg.ResultsRoot))
            errs.Add("results_root 为空");
        // fct_ini_path 可为空（未找到时提示，但不算错误）
        foreach (var p in cfg.Peers)
        {
            if (!Uri.TryCreate(p, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                errs.Add($"peer 非法: {p}");
        }
        if (cfg.DeviceAlertDiskFreeGb < 0 || cfg.DeviceAlertDiskFreeGb > 1024)
            errs.Add($"device_alert_disk_free_gb 越界: {cfg.DeviceAlertDiskFreeGb} (0-1024)");
        if (cfg.DeviceAlertCpuPct < 0 || cfg.DeviceAlertCpuPct > 100)
            errs.Add($"device_alert_cpu_pct 越界: {cfg.DeviceAlertCpuPct} (0-100)");
        if (cfg.DeviceAlertOfflineMinutes < 0 || cfg.DeviceAlertOfflineMinutes > 1440)
            errs.Add($"device_alert_offline_minutes 越界: {cfg.DeviceAlertOfflineMinutes}");
        if (cfg.YieldAlertYieldPct < 0 || cfg.YieldAlertYieldPct > 100)
            errs.Add($"yield_alert_yield_pct 越界: {cfg.YieldAlertYieldPct} (0-100)");
        if (cfg.TodoScanDays < 1 || cfg.TodoScanDays > 365)
            errs.Add($"todo_scan_days 越界: {cfg.TodoScanDays}");
        if (cfg.DbMaintenanceHour < 0 || cfg.DbMaintenanceHour > 23)
            errs.Add($"db_maintenance_hour 越界: {cfg.DbMaintenanceHour}");
        return errs;
    }
}
