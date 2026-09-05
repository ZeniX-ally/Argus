namespace FctAggregator;

public static class HighlightEngine
{
    public sealed class HighlightItem
    {
        public string Machine = "";
        public string Reason = "";
        public string Level = "";
    }

    public static List<HighlightItem> GetHighlights(AggDatabase db, AppConfig cfg)
    {
        if (cfg.HealthScoreEnabled)
        {
            try
            {
                var report = DeviceHealthScorer.Score(db, cfg);
                var hl = report.Machines
                    .Where(x => x.Level != "ok")
                    .Select(x => new HighlightItem
                    {
                        Machine = x.Machine,
                        Level = x.Level,
                        Reason = $"健康分 {x.Health:0}（{x.TopConcern} 子项最低：{x.Recommendation}）",
                    })
                    .ToList();
                if (hl.Count > 0 || report.Machines.Count > 0)
                {
                    try
                    {
                        var preds = db.QueryDevicePredicts(null, 100);
                        foreach (var p in preds.Where(x => x.level == "critical").Take(5))
                        {
                            if (!hl.Any(x => x.Machine == p.machine))
                                hl.Add(new HighlightItem { Machine = p.machine, Level = "critical", Reason = p.detail });
                        }
                    }
                    catch { }
                    return hl.GroupBy(x => x.Machine, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
                }
            }
            catch {  }
        }
        var list = new List<HighlightItem>();
        try{
            var infos = db.ListDeviceInfos();
            foreach(var info in infos){
                if(cfg.DeviceAlertDiskFreeGb>0 && info.DiskFreeGb>0 && info.DiskFreeGb < cfg.DeviceAlertDiskFreeGb)
                    list.Add(new HighlightItem{ Machine=info.Machine, Level="warn", Reason=$"磁盘 {info.DiskFreeGb:0.0}GB < 阈值 {cfg.DeviceAlertDiskFreeGb}"});
                if(cfg.DeviceAlertCpuPct>0 && info.CpuUsage >= cfg.DeviceAlertCpuPct)
                    list.Add(new HighlightItem{ Machine=info.Machine, Level="critical", Reason=$"CPU {info.CpuUsage:0.0}% >= {cfg.DeviceAlertCpuPct}%"});
                if(cfg.DeviceAlertOfflineMinutes>0 && DateTime.TryParse(info.LastSeen, out var dt)){
                    if((DateTime.Now - dt).TotalMinutes >= cfg.DeviceAlertOfflineMinutes)
                        list.Add(new HighlightItem{ Machine=info.Machine, Level="warn", Reason=$"离线 {(DateTime.Now-dt).TotalMinutes:0} 分钟"});
                }
            }
        } catch{}
        try{
            var preds = db.QueryDevicePredicts(null, 100);
            foreach(var p in preds.Where(x=> x.level=="critical").Take(5)){
                if(!list.Any(x=> x.Machine==p.machine))
                    list.Add(new HighlightItem{ Machine=p.machine, Level="critical", Reason=p.detail});
            }
        } catch{}
        return list.GroupBy(x=> x.Machine, StringComparer.OrdinalIgnoreCase).Select(g=> g.First()).ToList();
    }
}
