namespace FctAggregator;

/// <summary>域8 预警高亮 — 异常机台置顶红边脉冲（v3.19.0 C）：基于 device_info 阈值 + predict_log。</summary>
public static class HighlightEngine
{
    public sealed class HighlightItem
    {
        public string Machine = "";
        public string Reason = "";
        public string Level = ""; // warn/critical
    }

    public static List<HighlightItem> GetHighlights(AggDatabase db, AppConfig cfg)
    {
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
        // 叠加预测 critical
        try{
            var preds = db.QueryDevicePredicts(null, 100);
            foreach(var p in preds.Where(x=> x.level=="critical").Take(5)){
                if(!list.Any(x=> x.Machine==p.machine))
                    list.Add(new HighlightItem{ Machine=p.machine, Level="critical", Reason=p.detail});
            }
        } catch{}
        // 去重
        return list.GroupBy(x=> x.Machine, StringComparer.OrdinalIgnoreCase).Select(g=> g.First()).ToList();
    }
}
