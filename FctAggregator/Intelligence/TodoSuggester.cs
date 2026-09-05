namespace FctAggregator;

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
        public int CalibratedScore;
    }

    public static List<SuggestItem> Suggest(AggDatabase db, int scanDays=30, Func<string, double>? factorOf = null)
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
                var wf = factorOf?.Invoke(t.GroupKey) ?? 1.0;
                var scored = PriorityScorer.Score(t.SortCount, machineCount, duration, wf);
                list.Add(new SuggestItem{
                    GroupKey=t.GroupKey, Title=t.Title, StationId=t.StationId,
                    FailCount=t.SortCount, MachineCount=machineCount, DurationDays=duration,
                    Priority=scored.Zh, Reason=scored.Reason,
                    CalibratedScore = scored.Score,
                });
            }
        } catch{}
        return factorOf == null
            ? list.OrderByDescending(x=> x.FailCount).ToList()
            : list.OrderByDescending(x=> x.CalibratedScore).ThenByDescending(x=> x.FailCount).ToList();
    }
}
