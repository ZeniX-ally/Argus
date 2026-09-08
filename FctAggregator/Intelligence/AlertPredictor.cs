namespace FctAggregator;

public static class AlertPredictor
{
    public sealed class PredictItem
    {
        public string Machine = "";
        public string Rule = "";
        public string Level = "";
        public double Current;
        public double Predicted;
        public string Detail = "";
    }

    public static List<PredictItem> Predict(AggDatabase db)
    {
        var list = new List<PredictItem>();
        try{
            var cfg = AppConfig.Instance;
            var rows = db.QueryDailyStats();
            var grouped = rows.GroupBy(r=> r.Machine);
            foreach(var g in grouped){
                var ordered = g.OrderBy(r=> r.TestDate).TakeLast(5).ToList();
                if(ordered.Count<3) continue;
                var yields = ordered.Select(r=> r.Total>0? (double)r.Pass/r.Total*100 : 100).ToList();
                bool declining = yields.Zip(yields.Skip(1), (a,b)=> b < a).Count(x=> x) >= 2;
                double last = yields.Last();
                double slope = (yields.Last() - yields.First()) / (yields.Count-1);
                double predicted = last + slope;
                if(declining && predicted < cfg.YieldAlertYieldPct && last >= cfg.YieldAlertYieldPct){
                    list.Add(new PredictItem{ Machine=g.Key, Rule="yield", Level="warn", Current=Math.Round(last,1), Predicted=Math.Round(predicted,1), Detail=$"良率下滑 {last:0.0}% → 预测 {predicted:0.0}% 跌破 {cfg.YieldAlertYieldPct}%"});
                }
                if(cfg.YieldSeasonalityEnabled){
                    try{
                        var seasonalAlerts = YieldDecomposer.PredictWithSeasonality(db, cfg, g.Key);
                        if(seasonalAlerts != null && seasonalAlerts.Count > 0) list.AddRange(seasonalAlerts);
                    }catch{}
                }
            }
        } catch{}

        try{
            var preds = DevicePredictor.Predict(db, 7);
            foreach(var p in preds){
                string rule = p.Metric=="disk" ? "disk" : p.Metric=="cpu" ? "cpu" : "offline";
                list.Add(new PredictItem{ Machine=p.Machine, Rule=rule, Level=p.Level, Current=p.Current, Predicted=p.Predicted, Detail=p.Detail});
            }
        } catch{}
        return list;
    }

    public static void LogPredictions(AggDatabase db, List<PredictItem> items)
    {
        foreach(var it in items){
            try{ db.InsertAlertPredictLog(it.Machine, it.Rule, it.Level, it.Current, it.Predicted, it.Detail);} catch{}
        }
    }
}
