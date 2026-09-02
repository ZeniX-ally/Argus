namespace FctAggregator;

/// <summary>
/// 规格 03 季节性/班次分解（v3.21.0）：STL 简化版（趋势 + 周期 + 残差），纯 C# 零 NuGet。
/// 观测 = 趋势(trend) + 周期(seasonal) + 残差(residual)，告警只用残差超 σ 倍数的显著异常。
/// 模式：Hourly=日内 24 时段（agg_records 小时粒度）；Daily=日内 24 时段但趋势用 7 日滑动平均；
/// Weekly=按星期几 7 时段（yld_daily 粒度），趋势用 28 日线性回归。
/// 默认关（yield_seasonality_enabled=false），开启后残差太小（sigma &lt; min_sigma）自动回退 v3.19.0 老逻辑。
/// </summary>
public static class YieldDecomposer
{
    public enum SeasonalityMode { Hourly, Daily, Weekly }

    public sealed class DecompositionResult
    {
        public string Machine = "";
        public SeasonalityMode Mode;
        public int DaysBack;
        public List<double> Trend = new();        // 长度 = DaysBack（无数据日为 NaN）
        public List<double> Seasonal = new();      // 长度 = 周期长度（24 或 7）
        public List<double> Residual = new();      // 长度 = DaysBack（无数据日为 NaN）
        public double OverallMean;
        public double Sigma;
        public double Epsilon;        // 本次分解使用的告警 σ 倍数（与 cfg 同步）
        public List<AnomalyPoint> Anomalies = new();
        public DateTime GeneratedAt;
    }

    public sealed class AnomalyPoint
    {
        public DateTime Date;
        public double Value;        // 当日良率
        public double Residual;     // 残差
        public double ZScore;       // 标准化后的残差
        public string Severity = "warn";  // warn / critical（|z| ≥ 2×epsilon）
    }

    /// <summary>
    /// 对单台机台做季节性分解。数据不足 7 天 → Anomalies 置空（其余分量用现有数据算）；
    /// 完全无数据 → OverallMean=100、Anomalies 空（与 v3.19.0 "无数据视为 100" 口径一致）。
    /// </summary>
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

        // ---- 1) 取数 ----
        double[] daily;      // 每天一个良率（无数据 NaN）
        double[][] hourly;   // [d][h] 小时良率（无数据 NaN），仅 Hourly/Daily 模式
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
                // batch_timestamp 缺失/不可解析 → 整日归并入当天日均值（粗糙 fallback，不污染小时桶）
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
            // 小时桶 → 良率
            for (int i = 0; i < daysBack; i++)
            {
                for (int h = 0; h < 24; h++)
                {
                    if (totalSum[i, h] > 0)
                        hourly[i][h] = (double)passSum[i, h] / totalSum[i, h] * 100;
                }
                // 纯日级数据（无小时戳）→ 直接作为当天日均值
                if (dayTotal[i] > 0)
                {
                    bool anyHour = false;
                    for (int h = 0; h < 24; h++) if (!double.IsNaN(hourly[i][h])) { anyHour = true; break; }
                    if (!anyHour) daily[i] = (double)dayPass[i] / dayTotal[i] * 100;
                }
            }
        }

        // ---- 2) 每日值 = 当天非空小时桶均值（Weekly 直接来自 yld_daily）----
        for (int i = 0; i < daysBack; i++)
        {
            if (mode == SeasonalityMode.Weekly) continue;
            var vals = new List<double>();
            for (int h = 0; h < 24; h++)
                if (!double.IsNaN(hourly[i][h])) vals.Add(hourly[i][h]);
            if (vals.Count > 0) daily[i] = vals.Average();
        }

        int dataDays = daily.Count(v => !double.IsNaN(v));

        // ---- 3) 全局均值（完全无数据 → 100，与 v3.19 一致）----
        var allVals = daily.Where(v => !double.IsNaN(v)).ToList();
        double overallMean = allVals.Count > 0 ? allVals.Average() : 100;
        res.OverallMean = overallMean;

        // ---- 4) 趋势 ----
        var trend = new double[daysBack];
        if (mode == SeasonalityMode.Hourly)
        {
            // 日内均值 = 当天观测均值
            for (int i = 0; i < daysBack; i++) trend[i] = daily[i];
        }
        else if (mode == SeasonalityMode.Daily)
        {
            // 7 日滑动平均（尾随窗口，头部用可用天数）
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
            // Weekly：28 日线性回归 y = a + b*x（仅用有数据的天）
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

        // ---- 5) 周期分量 ----
        // Hourly/Daily：桶均值 − 全局均值（规格 §3.2 字面公式，日内趋势近似常量时二者等价）
        // Weekly：去趋势后分桶均值（规格 §3.3 的"线性回归趋势"若配字面公式，周一/周末低点会被系统性
        //        误判为异常——规格 §8 断言 5 要求不误报，故回归趋势下必须用去趋势均值，标准 STL 做法）
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

        // ---- 6) 残差：观测 − 趋势 − 周期（每天取非空桶均值）----
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

        // ---- 7) σ 与异常判定 ----
        var rvals = residual.Where(v => !double.IsNaN(v)).ToList();
        double sigma = 0, meanR = 0;
        if (rvals.Count >= 2)
        {
            meanR = rvals.Average();
            double varSum = rvals.Sum(v => (v - meanR) * (v - meanR));
            sigma = Math.Sqrt(varSum / rvals.Count);
        }
        res.Sigma = sigma;

        // 数据不足 7 天 → 不判异常（规格 §9）
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

    /// <summary>
    /// 被 AlertPredictor 调用：开关关 / 残差平稳（sigma &lt; min_sigma）/ 无数据 → 返回 null（调用方走 v3.19.0 老逻辑）。
    /// 开启且残差显著偏离 + 预测跌破阈值 → 返回季节性告警条目（yield/warn）。
    /// </summary>
    public static List<AlertPredictor.PredictItem>? PredictWithSeasonality(AggDatabase db, AppConfig cfg, string machine)
    {
        try
        {
            if (!cfg.YieldSeasonalityEnabled) return null;
            var mode = ParseMode(cfg.YieldSeasonalityMode);
            var dec = Decompose(db, machine, mode, cfg.YieldSeasonalityDays, cfg.YieldSeasonalityTrendWindow, cfg.YieldSeasonalityEpsilon);
            // 残差太小（数据平稳）：全局阈值判断更合适，回退老逻辑
            if (dec.Sigma < cfg.YieldSeasonalityMinSigma) return null;
            if (dec.Residual.Count == 0 || dec.Trend.Count == 0) return null;
            double latestResidual = dec.Residual[^1];
            double latestTrend = dec.Trend[^1];
            if (double.IsNaN(latestResidual) || double.IsNaN(latestTrend)) return null;

            // 用最近一天的残差 + 趋势预测明天的良率（季节分量已叠加在 trend 口径内）
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

    /// <summary>配置串 → 模式；非法回退 Hourly 并告警（规格 §9）。</summary>
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
