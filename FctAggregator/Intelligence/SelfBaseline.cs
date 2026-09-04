using System.Globalization;
using System.Text.Json;

namespace FctAggregator;

/// <summary>
/// 时段段（规格 05 §2.2：四段制，不做 24 小时分桶）。
/// 夜 00-06 / 早 06-12 / 午 12-18 / 晚 18-24 —— 工程依据：FCT 产线测试集中在白班，
/// 24 小时分桶夜间桶样本过稀永远冷启动；四段保证每桶可学习。
/// </summary>
public static class TimeSlot
{
    public const int Count = 4;

    /// <summary>小时 → 时段段号 0..3（越界钳制）。</summary>
    public static int SlotOfHour(int hour) => Math.Clamp(hour / 6, 0, Count - 1);

    public static string NameOf(int slot) => slot switch
    {
        0 => "夜(00-06)",
        1 => "早(06-12)",
        2 => "午(12-18)",
        _ => "晚(18-24)",
    };
}

/// <summary>一个「型号 × 时段段」基线桶的滚动统计（窗口内逐日取值再聚合）。</summary>
public sealed class BaselineBucketStat
{
    public string Model { get; set; } = "";
    public int Slot { get; set; }
    /// <summary>窗口内有样本的天数。</summary>
    public int DayCount { get; set; }
    /// <summary>窗口内 SN 去重后的总件数（冷启动判定依据）。</summary>
    public int SampleCount { get; set; }
    /// <summary>日良率均值 %（pass/(pass+fail)，中断不计入良率口径）。</summary>
    public double YieldMean { get; set; }
    /// <summary>日良率总体标准差。</summary>
    public double YieldSigma { get; set; }
}

/// <summary>一个时段段的全局（跨型号）日中断率基线——中断独立通道，不混入良率学习。</summary>
public sealed class SlotInterruptStat
{
    public int Slot { get; set; }
    public int DayCount { get; set; }
    public int SampleCount { get; set; }
    public double RateMean { get; set; }
    public double RateSigma { get; set; }
}

/// <summary>基线预警条目（只标记不强动作；每条必带期望区间与实际值）。</summary>
public sealed class BaselineAlert
{
    /// <summary>yield_drop = 良率跌破基线；interrupt_hotzone = 中断率升破基线（时段热区）。</summary>
    public string Kind { get; set; } = "";
    public string Date { get; set; } = "";
    public string Model { get; set; } = "";
    public int Slot { get; set; }
    public double Actual { get; set; }
    public double Mean { get; set; }
    public double Sigma { get; set; }
    public double ExpectedLow { get; set; }
    public double ExpectedHigh { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>基线状态快照（落 app_meta 的 learn_baseline_state，设置页可查可重置）。</summary>
public sealed class BaselineState
{
    public string ComputedAt { get; set; } = "";
    public string TodayYmd { get; set; } = "";
    public int WindowDays { get; set; }
    public int MinSamples { get; set; }
    public double SigmaK { get; set; }
    public List<BaselineBucketStat> Buckets { get; set; } = new();
    public List<SlotInterruptStat> SlotInterrupts { get; set; } = new();
    public List<BaselineAlert> Alerts { get; set; } = new();

    public string ToJson() => JsonSerializer.Serialize(this);

    public static BaselineState? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<BaselineState>(json); }
        catch { return null; }
    }
}

/// <summary>
/// 规格 05 S3：机台端滚动基线核心（纯函数，全部可测）。
/// 口径（依据 G49 spec 与规格决策）：
/// · 分桶 = 型号 × 时段段（四段），滚动 learn_baseline_window_days 天窗口；
/// · SN 去重：同 (日, 桶, SN) 多次记录取最新一条的结果（复测非独立样本）；
/// · 良率 = pass/(pass+fail)，INTERRUPTED 独立通道不混入；
/// · 预警 = 今日良率跌破 mean−k·σ（只报跌不报涨）/ 今日中断率升破 mean+k·σ（时段热区）；
/// · σ 下限 1.0 个百分点（零 σ 防误报，借鉴规格 03 yield_seasonality_min_sigma 经验）；
/// · 冷启动：桶窗口样本 &lt; learn_baseline_min_samples 不产出预警（宁缺勿滥）。
/// </summary>
public static class SelfBaseline
{
    /// <summary>σ 有效下限（百分点）。窗口内日良率完全一致（σ=0）时防止单件波动即触发预警。</summary>
    public const double SigmaFloor = 1.0;

    /// <summary>解析 test_date 的两种落库格式（生产 yyyyMMdd / 自检与部分链路 yyyy-MM-dd）。失败返回 null。</summary>
    public static DateTime? ParseYmd(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        if (DateTime.TryParseExact(t, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1)) return d1;
        if (DateTime.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2)) return d2;
        return null;
    }

    /// <summary>
    /// SN 去重：同 (日, 时段段, 型号, SN) 只保留 id 最大（最新）的一条结果。
    /// SN 为空的记录各自独立（无法归属同一块板，不参与去重合并）。
    /// </summary>
    public static List<BaselineSourceRecord> DedupBySn(IEnumerable<BaselineSourceRecord> records)
    {
        var deduped = new List<BaselineSourceRecord>();
        var bySn = new Dictionary<(string Date, int Slot, string Model, string Sn), BaselineSourceRecord>();
        foreach (var r in records)
        {
            if (string.IsNullOrWhiteSpace(r.Sn))
            {
                deduped.Add(r);
                continue;
            }
            var key = (r.TestDate, r.Hour, r.Model ?? "", r.Sn.Trim());
            if (!bySn.TryGetValue(key, out var cur) || r.Id > cur.Id)
                bySn[key] = r;
        }
        deduped.AddRange(bySn.Values);
        return deduped;
    }

    /// <summary>
    /// 一步计算：全窗口源记录 → 基线快照（分桶统计 + 今日预警）。
    /// baselineDays = 窗口内除今日外的日期；今日记录仅用于评估不进基线。
    /// </summary>
    public static BaselineState Compute(IEnumerable<BaselineSourceRecord> records, AppConfig cfg, DateTime? now = null)
    {
        var today = (now ?? DateTime.Now).Date;
        var windowDays = Math.Max(1, cfg.LearnBaselineWindowDays);
        var minSamples = Math.Max(1, cfg.LearnBaselineMinSamples);
        var sigmaK = cfg.LearnBaselineSigma > 0 ? cfg.LearnBaselineSigma : 3.0;

        var deduped = DedupBySn(records);
        var baselineRecs = deduped.Where(r => ParseYmd(r.TestDate) is { } d && d < today).ToList();
        var todayRecs = deduped.Where(r => ParseYmd(r.TestDate) == today).ToList();

        var buckets = BuildBuckets(baselineRecs);
        var slotInterrupts = BuildSlotInterruptBaseline(baselineRecs);
        var alerts = EvaluateToday(todayRecs, buckets, slotInterrupts, sigmaK, minSamples, today);

        return new BaselineState
        {
            ComputedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            TodayYmd = today.ToString("yyyy-MM-dd"),
            WindowDays = windowDays,
            MinSamples = minSamples,
            SigmaK = sigmaK,
            Buckets = buckets,
            SlotInterrupts = slotInterrupts,
            Alerts = alerts,
        };
    }

    /// <summary>构建「型号 × 时段段」基线桶（逐日良率取均值与总体标准差）。</summary>
    public static List<BaselineBucketStat> BuildBuckets(List<BaselineSourceRecord> baselineRecs)
    {
        var buckets = new List<BaselineBucketStat>();
        foreach (var grp in baselineRecs.GroupBy(r => (r.Model ?? "", TimeSlot.SlotOfHour(r.Hour))))
        {
            var dayStats = grp.GroupBy(r => r.TestDate)
                .Select(dg =>
                {
                    int pass = 0, fail = 0, intr = 0;
                    foreach (var r in dg)
                    {
                        if (string.Equals(r.Result, "PASS", StringComparison.OrdinalIgnoreCase)) pass++;
                        else if (string.Equals(r.Result, "FAIL", StringComparison.OrdinalIgnoreCase)) fail++;
                        else intr++;
                    }
                    return (Date: dg.Key, Pass: pass, Fail: fail, Intr: intr);
                })
                .ToList();

            var yields = dayStats.Where(d => d.Pass + d.Fail > 0)
                .Select(d => (double)d.Pass / (d.Pass + d.Fail) * 100.0)
                .ToList();

            buckets.Add(new BaselineBucketStat
            {
                Model = grp.Key.Item1,
                Slot = grp.Key.Item2,
                DayCount = dayStats.Count,
                SampleCount = grp.Count(),
                YieldMean = Mean(yields),
                YieldSigma = Std(yields),
            });
        }
        return buckets;
    }

    /// <summary>构建时段段全局（跨型号）日中断率基线。</summary>
    public static List<SlotInterruptStat> BuildSlotInterruptBaseline(List<BaselineSourceRecord> baselineRecs)
    {
        var stats = new List<SlotInterruptStat>();
        foreach (var grp in baselineRecs.GroupBy(r => TimeSlot.SlotOfHour(r.Hour)))
        {
            var dayRates = grp.GroupBy(r => r.TestDate)
                .Select(dg =>
                {
                    int total = dg.Count();
                    int intr = dg.Count(r => !string.Equals(r.Result, "PASS", StringComparison.OrdinalIgnoreCase)
                                          && !string.Equals(r.Result, "FAIL", StringComparison.OrdinalIgnoreCase));
                    return total > 0 ? (double)intr / total * 100.0 : -1.0;
                })
                .Where(x => x >= 0)
                .ToList();

            stats.Add(new SlotInterruptStat
            {
                Slot = grp.Key,
                DayCount = dayRates.Count,
                SampleCount = grp.Count(),
                RateMean = Mean(dayRates),
                RateSigma = Std(dayRates),
            });
        }
        return stats;
    }

    /// <summary>
    /// 评估今日：良率跌破 mean−k·σ 预警（只报跌）；中断率升破 mean+k·σ 时段热区预警（只报升）。
    /// 冷启动桶（窗口样本 &lt; minSamples）不产出预警。
    /// </summary>
    public static List<BaselineAlert> EvaluateToday(
        List<BaselineSourceRecord> todayRecs,
        List<BaselineBucketStat> buckets,
        List<SlotInterruptStat> slotInterrupts,
        double sigmaK,
        int minSamples,
        DateTime today)
    {
        var alerts = new List<BaselineAlert>();
        var dateYmd = today.ToString("yyyy-MM-dd");
        var bucketMap = buckets.ToDictionary(b => (b.Model, b.Slot));

        // 1) 型号 × 时段段 良率跌破
        foreach (var grp in todayRecs.GroupBy(r => (r.Model ?? "", TimeSlot.SlotOfHour(r.Hour))))
        {
            int pass = 0, fail = 0, intr = 0;
            foreach (var r in grp)
            {
                if (string.Equals(r.Result, "PASS", StringComparison.OrdinalIgnoreCase)) pass++;
                else if (string.Equals(r.Result, "FAIL", StringComparison.OrdinalIgnoreCase)) fail++;
                else intr++;
            }
            if (pass + fail <= 0) continue;   // 纯中断日不评良率（走中断通道）
            double yieldPct = (double)pass / (pass + fail) * 100.0;

            if (!bucketMap.TryGetValue(grp.Key, out var b)) continue;        // 无基线（新型号/新时段）不预警
            if (b.SampleCount < minSamples) continue;                        // 冷启动宁缺勿滥
            double effSigma = Math.Max(b.YieldSigma, SigmaFloor);
            double low = b.YieldMean - sigmaK * effSigma;
            if (yieldPct < low)
            {
                alerts.Add(new BaselineAlert
                {
                    Kind = "yield_drop",
                    Date = dateYmd,
                    Model = grp.Key.Item1,
                    Slot = grp.Key.Item2,
                    Actual = Math.Round(yieldPct, 2),
                    Mean = Math.Round(b.YieldMean, 2),
                    Sigma = Math.Round(b.YieldSigma, 2),
                    ExpectedLow = Math.Round(low, 2),
                    ExpectedHigh = Math.Round(b.YieldMean + sigmaK * effSigma, 2),
                    Message = $"自基线异常：型号 {grp.Key.Item1} {TimeSlot.NameOf(grp.Key.Item2)} 今日良率 {yieldPct:F1}% " +
                              $"低于基线 {b.YieldMean:F1}%−{sigmaK:F1}σ（期望 ≥ {low:F1}%，窗口 {b.SampleCount} 件/{b.DayCount} 天），" +
                              $"建议结合当日不良项排行排查。",
                });
            }
        }

        // 2) 时段段全局中断率热区
        foreach (var grp in todayRecs.GroupBy(r => TimeSlot.SlotOfHour(r.Hour)))
        {
            int total = grp.Count();
            if (total <= 0) continue;
            int intr = grp.Count(r => !string.Equals(r.Result, "PASS", StringComparison.OrdinalIgnoreCase)
                                   && !string.Equals(r.Result, "FAIL", StringComparison.OrdinalIgnoreCase));
            if (intr <= 0) continue;
            double rate = (double)intr / total * 100.0;

            var s = slotInterrupts.FirstOrDefault(x => x.Slot == grp.Key);
            if (s == null || s.SampleCount < minSamples) continue;           // 冷启动
            double effSigma = Math.Max(s.RateSigma, SigmaFloor);
            double high = s.RateMean + sigmaK * effSigma;
            if (rate > high)
            {
                alerts.Add(new BaselineAlert
                {
                    Kind = "interrupt_hotzone",
                    Date = dateYmd,
                    Model = "",
                    Slot = grp.Key,
                    Actual = Math.Round(rate, 2),
                    Mean = Math.Round(s.RateMean, 2),
                    Sigma = Math.Round(s.RateSigma, 2),
                    ExpectedLow = Math.Round(s.RateMean - sigmaK * effSigma, 2),
                    ExpectedHigh = Math.Round(high, 2),
                    Message = $"中断热区：{TimeSlot.NameOf(grp.Key)} 今日中断率 {rate:F1}% " +
                              $"高于基线 {s.RateMean:F1}%+{sigmaK:F1}σ（期望 ≤ {high:F1}%），" +
                              $"中断多与治具/通信/操作相关（规格 §2.4 中断型），建议核查该时段治具状态。",
                });
            }
        }

        return alerts;
    }

    private static double Mean(List<double> xs) => xs.Count == 0 ? 0 : xs.Average();

    /// <summary>总体标准差（÷N）；空/单点返回 0（评估侧用 SigmaFloor 兜底）。</summary>
    private static double Std(List<double> xs)
    {
        if (xs.Count <= 1) return 0;
        var m = xs.Average();
        return Math.Sqrt(xs.Sum(x => (x - m) * (x - m)) / xs.Count);
    }
}
