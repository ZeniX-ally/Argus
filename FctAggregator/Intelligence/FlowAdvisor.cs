namespace FctAggregator;

public static class FlowAdvisor
{
    public sealed class AdviseItem
    {
        public string Current = "";
        public string Suggested = "";
        public double Prob;
        public string Reason = "";
    }

    public static AdviseItem Advise(string current, AggDatabase db)
    {
        var counts = new Dictionary<string,int>();
        var nextCounts = new Dictionary<string, Dictionary<string,int>>();
        try{
            var all = db.ListMaintenance("", 1000);
            var grouped = all.GroupBy(x=> (x.StationId+"|"+x.FailItem));
            foreach(var g in grouped){
                var ordered = g.OrderBy(x=> x.UpdatedAt).ToList();
                for(int i=1;i<ordered.Count;i++){
                    var from = MaintenanceMeta.Normalize(ordered[i-1].Status);
                    var to = MaintenanceMeta.Normalize(ordered[i].Status);
                    if(from==to) continue;
                    if(!nextCounts.TryGetValue(from, out var dic)) nextCounts[from]=dic=new Dictionary<string,int>();
                    dic[to]=dic.GetValueOrDefault(to)+1;
                    counts[from]=counts.GetValueOrDefault(from)+1;
                }
            }
        } catch{}

        string cur = MaintenanceMeta.Normalize(current);
        if(nextCounts.TryGetValue(cur, out var nxt) && nxt.Count>0){
            var best = nxt.OrderByDescending(kv=>kv.Value).First();
            double prob = counts[cur]>0 ? (double)best.Value / counts[cur] : 0;
            return new AdviseItem{ Current=cur, Suggested=best.Key, Prob=Math.Round(prob,2), Reason=$"历史 {counts[cur]} 次中 {best.Value} 次流转到 {best.Key}"};
        }
        string def = cur=="open" ? "in_progress" : cur=="in_progress" ? "resolved" : cur;
        return new AdviseItem{ Current=cur, Suggested=def, Prob=0.5, Reason="默认路径"};
    }

    public static AdviseItem AdviseLocal(string current, Database db)
    {
        var counts = new Dictionary<string,int>();
        var nextCounts = new Dictionary<string, Dictionary<string,int>>();
        try{
            var all = db.ListMaintenance("", 1000);
            var grouped = all.GroupBy(x=> (x.StationId+"|"+x.FailItem));
            foreach(var g in grouped){
                var ordered = g.OrderBy(x=> x.UpdatedAt).ToList();
                for(int i=1;i<ordered.Count;i++){
                    var from = MaintenanceMeta.Normalize(ordered[i-1].Status);
                    var to = MaintenanceMeta.Normalize(ordered[i].Status);
                    if(from==to) continue;
                    if(!nextCounts.TryGetValue(from, out var dic)) nextCounts[from]=dic=new Dictionary<string,int>();
                    dic[to]=dic.GetValueOrDefault(to)+1;
                    counts[from]=counts.GetValueOrDefault(from)+1;
                }
            }
        } catch{}
        string cur = MaintenanceMeta.Normalize(current);
        if(nextCounts.TryGetValue(cur, out var nxt) && nxt.Count>0){
            var best = nxt.OrderByDescending(kv=>kv.Value).First();
            double prob = counts[cur]>0 ? (double)best.Value / counts[cur] : 0;
            return new AdviseItem{ Current=cur, Suggested=best.Key, Prob=Math.Round(prob,2), Reason=$"历史 {counts[cur]} 次中 {best.Value} 次流转"};
        }
        string def = cur=="open" ? "in_progress" : cur=="in_progress" ? "resolved" : cur;
        return new AdviseItem{ Current=cur, Suggested=def, Prob=0.5, Reason="默认路径"};
    }
}
