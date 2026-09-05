namespace FctAggregator;

/// <summary>规格06：CPK 过程能力（纯函数）。单/双边限由 lolim/hilim 非空判定；σ=0 视为常数量测项不参与能力评级。</summary>
public static class CpkAnalyzer
{
    public static double MarginRatio(double mean, double? lolim, double? hilim, double sigma)
    {
        if (sigma <= 0) return double.MaxValue;
        double threeSigma = 3 * sigma;
        double? cpk = null;
        if (hilim.HasValue) cpk = (hilim.Value - mean) / threeSigma;
        if (lolim.HasValue)
        {
            var c2 = (mean - lolim.Value) / threeSigma;
            cpk = cpk.HasValue ? Math.Min(cpk.Value, c2) : c2;
        }
        return cpk ?? double.MaxValue;
    }

    public static List<CpkItem> Compute(List<MeasureStatsRow> stats)
    {
        var items = new List<CpkItem>();
        foreach (var s in stats)
        {
            var item = new CpkItem
            {
                Model = s.Model,
                TestName = s.TestName,
                N = s.N,
                Unit = s.Unit,
                Lolim = s.Lolim,
                Hilim = s.Hilim,
            };
            if (s.Sigma <= 0)
            {
                item.Level = "constant";
            }
            else if (!s.Hilim.HasValue && !s.Lolim.HasValue)
            {
                item.Level = "no_limit";
            }
            else
            {
                var cpk = MarginRatio(s.Mean, s.Lolim, s.Hilim, s.Sigma);
                item.Cpk = Math.Round(cpk, 3);
                item.Level = cpk < AnalysisEngine.CpkLowThreshold ? "low" : "ok";
            }
            items.Add(item);
        }
        return items;
    }
}
