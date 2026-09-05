namespace FctAggregator;

public static class AlertHealer
{
    public sealed class HealItem
    {
        public string Machine = "";
        public string AlertRule = "";
        public string Suggestion = "";
        public string Detail = "";
    }

    public static List<HealItem> Heal(AggDatabase db, string machine, string rule)
    {
        var list = new List<HealItem>();
        var inspects = DeviceInspector.Inspect(db);
        foreach(var ins in inspects.Where(x=> x.Machine==machine)){
            list.Add(new HealItem{ Machine=machine, AlertRule=rule, Suggestion=ins.Suggestion, Detail=ins.Detail});
        }
        if(list.Count==0){
            string sug = rule=="disk" ? "清理 data/archive 或扩容" : rule=="cpu" ? "检查高 CPU 进程/重启服务" : rule=="offline" ? "检查网络/服务存活" : "检查良率相关机台与治具";
            list.Add(new HealItem{ Machine=machine, AlertRule=rule, Suggestion=sug, Detail="自动巡检生成" });
        }
        return list;
    }

    public static string FormatForFeishu(string machine, string rule, List<HealItem> items)
    {
        if(items.Count==0) return "";
        return $"自愈建议 ({machine}/{rule}): " + string.Join("；", items.Select(x=> x.Suggestion+"("+x.Detail+")"));
    }
}
