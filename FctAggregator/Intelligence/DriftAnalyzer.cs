namespace FctAggregator;

/// <summary>规格06：规格带漂移预警（纯函数）。基线=窗口期(不含今日)，对照今日均值漂移与规格限余量。</summary>
public static class DriftAnalyzer
{
    public const int MinTodaySamples = 3;

    public static List<DriftAlertItem> Compute(
        List<MeasureStatsRow> baseStats, List<MeasureStatsRow> todayStats, AppConfig cfg)
    {
        var result = new List<DriftAlertItem>();
        var baseMap = new Dictionary<(string, string), MeasureStatsRow>();
        foreach (var b in baseStats)
            if (b.N >= cfg.AnalyzeMinSamples)
                baseMap[(b.Model, b.TestName)] = b;

        foreach (var t in todayStats)
        {
            if (t.N < MinTodaySamples) continue;
            if (!baseMap.TryGetValue((t.Model, t.TestName), out var b)) continue;

            double range = b.Hilim.HasValue && b.Lolim.HasValue && b.Hilim.Value > b.Lolim.Value
                ? b.Hilim.Value - b.Lolim.Value
                : Math.Abs(b.Mean);
            double sigmaFloor = range * 0.01;
            double sigmaEff = Math.Max(b.Sigma, sigmaFloor);
            if (sigmaEff <= 0) continue;

            var item = new DriftAlertItem
            {
                Model = t.Model,
                TestName = t.TestName,
                TodayMean = t.Mean,
                BaseMean = b.Mean,
                SigmaEff = sigmaEff,
                Unit = t.Unit,
                Lolim = t.Lolim,
                Hilim = t.Hilim,
                NBase = b.N,
                NToday = t.N,
            };

            double z = Math.Abs(t.Mean - b.Mean) / sigmaEff;
            if (z > cfg.AnalyzeDriftSigma)
            {
                item.Kind = "drift";
                item.Score = Math.Round(z, 2);
                item.ExpectedLow = b.Mean - cfg.AnalyzeDriftSigma * sigmaEff;
                item.ExpectedHigh = b.Mean + cfg.AnalyzeDriftSigma * sigmaEff;
                item.Message = $"均值漂移: 今日 {Fmt(t.Mean)}{UnitOf(t)} 偏离基线 {Fmt(b.Mean)}（{b.N} 样本），" +
                               $"期望 {cfg.AnalyzeDriftSigma}σ 区间 [{Fmt(item.ExpectedLow)}, {Fmt(item.ExpectedHigh)}]";
                result.Add(item);
            }
            else if (t.Hilim.HasValue || t.Lolim.HasValue)
            {
                double margin = CpkAnalyzer.MarginRatio(t.Mean, t.Lolim, t.Hilim, sigmaEff);
                if (margin < cfg.AnalyzeDriftWarnRatio)
                {
                    item.Kind = "margin";
                    item.Score = Math.Round(margin, 2);
                    item.Message = $"逼近规格限: 今日均值 {Fmt(t.Mean)}{UnitOf(t)} 距限余量仅 {margin:F2}×3σ" +
                                   $"（限 [{FmtOr(t.Lolim)}, {FmtOr(t.Hilim)}]）";
                    result.Add(item);
                }
            }
        }

        return result
            .OrderByDescending(a => a.Kind == "drift" ? 1 : 0)
            .ThenByDescending(a => a.Score)
            .ToList();
    }

    internal static string UnitOf(MeasureStatsRow r) => string.IsNullOrWhiteSpace(r.Unit) ? "" : r.Unit;
    internal static string Fmt(double v) => v.ToString("F4");
    internal static string FmtOr(double? v) => v.HasValue ? Fmt(v.Value) : "∞";
}
