namespace FctAggregator;

/// <summary>域7 待办智能建议（v3.19.0 A/C）：机台/型号上下文 + 优先级 + 去重建议。</summary>
public static class TodoSuggester
{
    public sealed class SuggestItem
    {
        public string GroupKey = "";
        public string Title = "";
        public string StationId = "";
        public int FailCount;
        public int MachineCount;
        public int DurationDays;
        public string Priority = "";
        public string Reason = "";
    }

    public static List<SuggestItem> Suggest(AggDatabase db, int scanDays=30)
    {
        var list = new List<SuggestItem>();
        try{
            var todos = db.ListTodoView();
            foreach(var t in todos){
                int duration = 1;
                try{
                    if(DateTime.TryParse(t.FirstSeen, out var f) && DateTime.TryParse(t.LastSeen, out var l))
                        duration = Math.Max(1, (int)(l - f).TotalDays + 1);
                } catch{}
                int machineCount = 1;
                try{ machineCount = todos.Count(x=> x.GroupKey==t.GroupKey); } catch{}
                var scored = PriorityScorer.Score(t.SortCount, machineCount, duration);
                list.Add(new SuggestItem{
                    GroupKey=t.GroupKey, Title=t.Title, StationId=t.StationId,
                    FailCount=t.SortCount, MachineCount=machineCount, DurationDays=duration,
                    Priority=scored.Zh, Reason=scored.Reason
                });
            }
        } catch{}
        return list.OrderByDescending(x=> x.FailCount).ToList();
    }
}
