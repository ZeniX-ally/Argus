namespace FctAggregator;

public static class ConfigAdvisor
{
    public sealed class RecommendItem
    {
        public string Key = "";
        public string Current = "";
        public string Suggested = "";
        public string Reason = "";
    }

    public static List<RecommendItem> Recommend(AppConfig cfg, AggDatabase aggDb)
    {
        var list = new List<RecommendItem>();
        try
        {
            var rows = aggDb.QueryDailyStats();
            if (rows.Count > 0)
            {
                var recent = rows.Where(r => {
                    if (r.TestDate.Length != 8) return false;
                    if (!DateTime.TryParseExact(r.TestDate, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d)) return false;
                    return (DateTime.Today - d).TotalDays <= 30;
                }).ToList();
                if (recent.Count >= 5)
                {
                    var avgYield = recent.Average(r => r.Total > 0 ? (double)r.Pass / r.Total * 100 : 100);
                    var p10 = recent.Select(r => r.Total > 0 ? (double)r.Pass / r.Total * 100 : 100).OrderBy(x=>x).ElementAt(recent.Count / 10);
                    var suggested = Math.Clamp(Math.Floor(p10 - 2), 80, 98);
                    if (Math.Abs(suggested - cfg.YieldAlertYieldPct) >= 3)
                        list.Add(new RecommendItem{ Key="yield_alert_yield_pct", Current=cfg.YieldAlertYieldPct.ToString("0.##"), Suggested=suggested.ToString("0"), Reason=$"近30天平均良率 {avgYield:0.0}%, P10={p10:0.0}%"} );
                }
            }
        } catch { }
        try
        {
            var infos = aggDb.ListDeviceInfos();
            if (infos.Count > 0)
            {
                var avgFree = infos.Where(i=>i.DiskFreeGb>0).Select(i=>i.DiskFreeGb).DefaultIfEmpty(0).Average();
                if (avgFree > 0 && avgFree < cfg.DeviceAlertDiskFreeGb * 1.5 && cfg.DeviceAlertDiskFreeGb > 5)
                    list.Add(new RecommendItem{ Key="device_alert_disk_free_gb", Current=cfg.DeviceAlertDiskFreeGb.ToString(), Suggested=Math.Max(5, (int)(avgFree*0.5)).ToString(), Reason=$"平均剩余 {avgFree:0.0}GB 接近阈值"} );
                var avgCpu = infos.Where(i=>i.CpuUsage>0).Select(i=>i.CpuUsage).DefaultIfEmpty(0).Average();
                if (avgCpu > 60 && cfg.DeviceAlertCpuPct > 80)
                    list.Add(new RecommendItem{ Key="device_alert_cpu_pct", Current=cfg.DeviceAlertCpuPct.ToString(), Suggested="85", Reason=$"平均 CPU {avgCpu:0.0}% 偏高"} );
            }
        } catch { }
        return list;
    }
}

