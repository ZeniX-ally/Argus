namespace FctAggregator;

/// <summary>
/// 规格06：CPK 过程能力（纯函数）。单/双边限由 lolim/hilim 非空判定。
/// G49 FCT spec 大量测项是设定值（电源轨 5V±3%、KL30_FILT 14.4V±0.2V、NTC 仿真电阻、旋变固定角），
/// σ 相对规格带极小——这类不参与能力评级，避免「贴着限」被当成低 CPK / 逼近限。
/// </summary>
public static class CpkAnalyzer
{
    /// <summary>过程波动门槛：观测 σ 至少占规格带宽（或 |mean|）的 2%，才视为过程量。设定值通常 ≪ 1%。</summary>
    public const double MinProcessSigmaRatio = 0.02;

    public static bool HasProcessVariation(MeasureStatsRow s)
    {
        if (s == null || s.Sigma <= 0) return false;
        double window = s.Hilim.HasValue && s.Lolim.HasValue && s.Hilim.Value > s.Lolim.Value
            ? s.Hilim.Value - s.Lolim.Value
            : Math.Abs(s.Mean);
        if (window <= 0)
            window = Math.Abs(s.Hilim ?? s.Lolim ?? 0);
        if (window <= 0) return false;
        return s.Sigma / window >= MinProcessSigmaRatio;
    }

    /// <summary>过程量判定：先排除注入/通信/中断语义，再要求 σ ≥ 规格带宽 2%。</summary>
    public static bool ShouldTreatAsProcess(string? testName, MeasureStatsRow s)
    {
        if (s == null) return false;
        if (G49ProductDictionary.IsNonProcessSemantic(testName ?? s.TestName)) return false;
        return HasProcessVariation(s);
    }

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
            bool hasLim = s.Hilim.HasValue || s.Lolim.HasValue;
            if (G49ProductDictionary.IsNonProcessSemantic(s.TestName))
            {
                item.Level = "constant";
            }
            else if (!hasLim)
            {
                item.Level = s.Sigma <= 0 ? "constant" : "no_limit";
            }
            else if (!HasProcessVariation(s))
            {
                item.Level = "constant";
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
