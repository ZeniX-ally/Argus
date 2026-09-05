namespace FctAggregator;

public static class YieldDecomposer
{
    public enum SeasonalityMode { Hourly, Daily, Weekly }

    public sealed class DecompositionResult
    {
        public string Machine = "";
        public SeasonalityMode Mode;
        public int DaysBack;
        public List<double> Trend = new();
        public List<double> Seasonal = new();
        public List<double> Residual = new();
        public double OverallMean;
        public double Sigma;
        public double Epsilon;
        public List<AnomalyPoint> Anomalies = new();
        public DateTime GeneratedAt;
    }

    public sealed class AnomalyPoint
    {
        public DateTime Date;
        public double Value;
        public double Residual;
        public double ZScore;
        public string Severity = "warn";
    }

    public static DecompositionResult Decompose(
        AggDatabase db,
        string machine,
        SeasonalityMode mode = SeasonalityMode.Hourly,
        int daysBack = 28,
        int trendWindow = 7,
        double epsilon = 1.5)
    {
        var res = new DecompositionResult
        {
            Machine = machine,
            Mode = mode,
            DaysBack = daysBack,
            Epsilon = epsilon,
            GeneratedAt = DateTime.Now
        };
        if (daysBack < 1) daysBack = 1;
        if (trendWindow < 1) trendWindow = 1;

        var today = DateTime.Today;
        var start = today.AddDays(-(daysBack - 1));
        string fromYmd = start.ToString("yyyyMMdd");
        string toYmd = today.ToString("yyyyMMdd");

        double[] daily;
        double[][] hourly;
        if (mode == SeasonalityMode.Weekly)
        {
            daily = new double[daysBack];
            for (int i = 0; i < daysBack; i++) daily[i] = double.NaN;
            foreach (var row in db.QueryDailyStats(machine, fromYmd, toYmd))
            {
                if (!DateTime.TryParseExact(row.TestDate, "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var dt)) continue;
                int idx = (dt - start).Days;
                if (idx < 0 || idx >= daysBack) continue;
                daily[idx] = row.Total > 0 ? (double)row.Pass / row.Total * 100 : 100;
            }
            hourly = Array.Empty<double[]>();
        }
        else
        {
            daily = new double[daysBack];
            hourly = new double[daysBack][];
            for (int i = 0; i < daysBack; i++)
            {
                daily[i] = double.NaN;
                hourly[i] = new double[24];
                for (int h = 0; h < 24; h++) hourly[i][h] = double.NaN;
            }
            var passSum = new long[daysBack, 24];
            var totalSum = new long[daysBack, 24];
            var dayPass = new long[daysBack];
            var dayTotal = new long[daysBack];
            foreach (var row in db.QueryHourlyRaw(machine, fromYmd, toYmd))
            {
                if (!DateTime.TryParseExact(row.TestDate, "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var dt)) continue;
                int idx = (dt - start).Days;
                if (idx < 0 || idx >= daysBack) continue;
                bool isPass = string.Equals(row.Result, "PASS", StringComparison.OrdinalIgnoreCase);
                int hour = -1;
                if (!string.IsNullOrEmpty(row.BatchTimestamp) &&
                    DateTime.TryParse(row.BatchTimestamp, out var bt)) hour = bt.Hour;
                if (hour >= 0 && hour < 24)
                {
                    if (isPass) passSum[idx, hour]++;
                    totalSum[idx, hour]++;
                }
                else
                {
                    if (isPass) dayPass[idx]++;
                    dayTotal[idx]++;
                }
            }
            for (int i = 0; i < daysBack; i++)
            {
                for (int h = 0; h < 24; h++)
                {
                    if (totalSum[i, h] > 0)
                        hourly[i][h] = (double)passSum[i, h] / totalSum[i, h] * 100;
                }
                if (dayTotal[i] > 0)
                {
                    bool anyHour = false;
                    for (int h = 0; h < 24; h++) if (!double.IsNaN(hourly[i][h])) { anyHour = true; break; }
                    if (!anyHour) daily[i] = (double)dayPass[i] / dayTotal[i] * 100;
                }
            }
        }

        for (int i = 0; i < daysBack; i++)
        {
            if (mode == SeasonalityMode.Weekly) continue;
            var vals = new List<double>();
            for (int h = 0; h < 24; h++)
                if (!double.IsNaN(hourly[i][h])) vals.Add(hourly[i][h]);
            if (vals.Count > 0) daily[i] = vals.Average();
        }

        int dataDays = daily.Count(v => !double.IsNaN(v));

        var allVals = daily.Where(v => !double.IsNaN(v)).ToList();
        double overallMean = allVals.Count > 0 ? allVals.Average() : 100;
        res.OverallMean = overallMean;

        var trend = new double[daysBack];
        if (mode == SeasonalityMode.Hourly)
        {
            for (int i = 0; i < daysBack; i++) trend[i] = daily[i];
        }
        else if (mode == SeasonalityMode.Daily)
        {
            for (int i = 0; i < daysBack; i++)
            {
                var win = new List<double>();
                for (int j = Math.Max(0, i - trendWindow + 1); j <= i; j++)
                    if (!double.IsNaN(daily[j])) win.Add(daily[j]);
                trend[i] = win.Count > 0 ? win.Average() : double.NaN;
            }
        }
        else
        {
            double b = 0, a = 0;
            var pts = new List<(double x, double y)>();
            for (int i = 0; i < daysBack; i++)
                if (!double.IsNaN(daily[i])) pts.Add((i, daily[i]));
            if (pts.Count >= 2)
            {
                double mx = pts.Average(p => p.x), my = pts.Average(p => p.y);
                double sxx = pts.Sum(p => (p.x - mx) * (p.x - mx));
                if (sxx > 1e-12)
                {
                    b = pts.Sum(p => (p.x - mx) * (p.y - my)) / sxx;
                    a = my - b * mx;
                }
                else { a = my; b = 0; }
            }
            else if (pts.Count == 1) { a = pts[0].y; b = 0; }
            for (int i = 0; i < daysBack; i++)
                trend[i] = pts.Count > 0 ? a + b * i : double.NaN;
        }

        int periodLen = mode == SeasonalityMode.Weekly ? 7 : 24;
        var seasonal = new double[periodLen];
        if (mode == SeasonalityMode.Weekly)
        {
            var byWeekday = new List<double>[7];
            for (int w = 0; w < 7; w++) byWeekday[w] = new List<double>();
            for (int i = 0; i < daysBack; i++)
                if (!double.IsNaN(daily[i]) && !double.IsNaN(trend[i]))
                    byWeekday[(int)(start.AddDays(i).DayOfWeek)].Add(daily[i] - trend[i]);
            for (int w = 0; w < 7; w++)
                seasonal[w] = byWeekday[w].Count > 0 ? byWeekday[w].Average() : 0;
        }
        else
        {
            var byHour = new List<double>[24];
            for (int h = 0; h < 24; h++) byHour[h] = new List<double>();
            for (int i = 0; i < daysBack; i++)
                for (int h = 0; h < 24; h++)
                    if (!double.IsNaN(hourly[i][h])) byHour[h].Add(hourly[i][h]);
            for (int h = 0; h < 24; h++)
                seasonal[h] = byHour[h].Count > 0 ? byHour[h].Average() - overallMean : 0;
        }

        var residual = new double[daysBack];
        for (int i = 0; i < daysBack; i++)
        {
            if (double.IsNaN(daily[i])) { residual[i] = double.NaN; continue; }
            if (mode == SeasonalityMode.Weekly)
            {
                residual[i] = daily[i] - trend[i] - seasonal[(int)start.AddDays(i).DayOfWeek];
            }
            else
            {
                var rs = new List<double>();
                for (int h = 0; h < 24; h++)
                    if (!double.IsNaN(hourly[i][h]))
                        rs.Add(hourly[i][h] - trend[i] - seasonal[h]);
                residual[i] = rs.Count > 0 ? rs.Average() : double.NaN;
            }
        }

        var rvals = residual.Where(v => !double.IsNaN(v)).ToList();
        double sigma = 0, meanR = 0;
        if (rvals.Count >= 2)
        {
            meanR = rvals.Average();
            double varSum = rvals.Sum(v => (v - meanR) * (v - meanR));
            sigma = Math.Sqrt(varSum / rvals.Count);
        }
        res.Sigma = sigma;

        bool enoughData = dataDays >= 7;
        if (enoughData)
        {
            var anomalies = new List<AnomalyPoint>();
            for (int i = 0; i < daysBack; i++)
            {
                if (double.IsNaN(residual[i])) continue;
                double z = (residual[i] - meanR) / (sigma + 1e-9);
                if (Math.Abs(z) > epsilon)
                {
                    anomalies.Add(new AnomalyPoint
                    {
                        Date = start.AddDays(i),
                        Value = daily[i],
                        Residual = residual[i],
                        ZScore = z,
                        Severity = Math.Abs(z) >= 2 * epsilon ? "critical" : "warn"
                    });
                }
            }
            res.Anomalies = anomalies;
        }

        res.Trend = trend.ToList();
        res.Seasonal = seasonal.ToList();
        res.Residual = residual.ToList();
        return res;
    }

    public static List<AlertPredictor.PredictItem>? PredictWithSeasonality(AggDatabase db, AppConfig cfg, string machine)
    {
        try
        {
            if (!cfg.YieldSeasonalityEnabled) return null;
            var mode = ParseMode(cfg.YieldSeasonalityMode);
            var dec = Decompose(db, machine, mode, cfg.YieldSeasonalityDays, cfg.YieldSeasonalityTrendWindow, cfg.YieldSeasonalityEpsilon);
            if (dec.Sigma < cfg.YieldSeasonalityMinSigma) return null;
            if (dec.Residual.Count == 0 || dec.Trend.Count == 0) return null;
            double latestResidual = dec.Residual[^1];
            double latestTrend = dec.Trend[^1];
            if (double.IsNaN(latestResidual) || double.IsNaN(latestTrend)) return null;

            double predicted = latestTrend + latestResidual;
            bool isAnomaly = Math.Abs(latestResidual) > cfg.YieldSeasonalityEpsilon * dec.Sigma;
            if (isAnomaly && predicted < cfg.YieldAlertYieldPct)
            {
                return new List<AlertPredictor.PredictItem>
                {
                    new AlertPredictor.PredictItem
                    {
                        Machine = machine,
                        Rule = "yield",
                        Level = "warn",
                        Current = Math.Round(latestTrend, 1),
                        Predicted = Math.Round(predicted, 1),
                        Detail = $"季节性分解: 残差 {latestResidual:0.00}pp 偏离基线 {dec.Sigma:0.00}σ → 预测 {predicted:0.00}% 跌破 {cfg.YieldAlertYieldPct}%"
                    }
                };
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"季节性分解失败，回退老逻辑: {ex.Message}");
        }
        return null;
    }

    public static SeasonalityMode ParseMode(string? mode)
    {
        switch ((mode ?? "").Trim().ToLowerInvariant())
        {
            case "daily": return SeasonalityMode.Daily;
            case "weekly": return SeasonalityMode.Weekly;
            case "hourly": return SeasonalityMode.Hourly;
            default:
                if (!string.IsNullOrWhiteSpace(mode))
                    Logger.Warning($"yield_seasonality_mode 非法 '{mode}'，回退 hourly");
                return SeasonalityMode.Hourly;
        }
    }
}
