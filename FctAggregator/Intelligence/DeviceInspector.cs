namespace FctAggregator;

public static class DeviceInspector
{
    public sealed class SuggestItem
    {
        public string Machine = "";
        public string Kind = "";
        public string Level = "";
        public string Detail = "";
        public string Suggestion = "";
    }

    public static List<SuggestItem> Inspect(AggDatabase db)
    {
        var list = new List<SuggestItem>();
        List<DeviceInfoRow> infos;
        try { infos = db.ListDeviceInfos(); } catch { return list; }
        foreach(var info in infos)
        {
            if(info.DiskFreeGb>0 && info.DiskFreeGb < 10)
            {
                var lv = info.DiskFreeGb < 5 ? "warn" : "info";
                list.Add(new SuggestItem{ Machine=info.Machine, Kind="disk", Level=lv, Detail=$"磁盘剩余 {info.DiskFreeGb:0.0}GB", Suggestion="清理 data/archive 或扩容"});
            }
            if(!string.IsNullOrEmpty(info.LastSeen) && DateTime.TryParse(info.LastSeen, out var dt))
            {
                if((DateTime.Now - dt).TotalMinutes > 5)
                    list.Add(new SuggestItem{ Machine=info.Machine, Kind="offline", Level="warn", Detail=$"离线 {(DateTime.Now-dt).TotalMinutes:0} 分钟", Suggestion="检查网络/服务是否存活"});
            }
        }
        try
        {
            var fcts = db.ListDeviceFcts();
            foreach(var f in fcts)
            {
                var row = db.GetDeviceFct(f.Machine);
                if(row==null) continue;
                foreach(var dev in row.Devices.Where(d=>d.Type=="com" && !d.Online))
                {
                    list.Add(new SuggestItem{ Machine=row.Machine, Kind="com", Level="info", Detail=$"COM {dev.Port} ({dev.Name}) 离线", Suggestion="重插串口线/检查驱动"});
                }
                if(!row.Found)
                    list.Add(new SuggestItem{ Machine=row.Machine, Kind="fct", Level="info", Detail="FCT.ini 未找到", Suggestion="检查 FCT.ini 路径配置"});
            }
        } catch {}
        return list;
    }
}
