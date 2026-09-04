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
        /// <summary>校准后分值（规格 05 S4：未注入 factorOf 时 = 原始分）。</summary>
        public int CalibratedScore;
    }

    /// <summary>
    /// 待办智能建议（规格 05 S4：factorOf 注入自学习校准因子——有因子时按校准分排序，无则保持原 failCount 排序）。
    /// </summary>
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
        // 校准因子参与排序（规格 §3.3：仅影响排序）；无 factorOf 时与 v3.23.3 行为一致
        return factorOf == null
            ? list.OrderByDescending(x=> x.FailCount).ToList()
            : list.OrderByDescending(x=> x.CalibratedScore).ThenByDescending(x=> x.FailCount).ToList();
    }
}
