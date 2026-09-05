using System.Globalization;
using System.Text.Json;

namespace FctAggregator;

public static class TimeSlot
{
    public const int Count = 4;

    public static int SlotOfHour(int hour) => Math.Clamp(hour / 6, 0, Count - 1);

    public static string NameOf(int slot) => slot switch
    {
        0 => "夜(00-06)",
        1 => "早(06-12)",
        2 => "午(12-18)",
        _ => "晚(18-24)",
    };
}

public sealed class BaselineBucketStat
{
    public string Model { get; set; } = "";
    public int Slot { get; set; }
    public int DayCount { get; set; }
    public int SampleCount { get; set; }
    public double YieldMean { get; set; }
    public double YieldSigma { get; set; }
}

public sealed class SlotInterruptStat
{
    public int Slot { get; set; }
    public int DayCount { get; set; }
    public int SampleCount { get; set; }
    public double RateMean { get; set; }
    public double RateSigma { get; set; }
}

public sealed class BaselineAlert
{
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

public static class SelfBaseline
{
    public const double SigmaFloor = 1.0;

    public static DateTime? ParseYmd(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        if (DateTime.TryParseExact(t, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1)) return d1;
        if (DateTime.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2)) return d2;
        return null;
    }

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

        foreach (var grp in todayRecs.GroupBy(r => (r.Model ?? "", TimeSlot.SlotOfHour(r.Hour))))
        {
            int pass = 0, fail = 0, intr = 0;
            foreach (var r in grp)
            {
                if (string.Equals(r.Result, "PASS", StringComparison.OrdinalIgnoreCase)) pass++;
                else if (string.Equals(r.Result, "FAIL", StringComparison.OrdinalIgnoreCase)) fail++;
                else intr++;
            }
            if (pass + fail <= 0) continue;
            double yieldPct = (double)pass / (pass + fail) * 100.0;

            if (!bucketMap.TryGetValue(grp.Key, out var b)) continue;
            if (b.SampleCount < minSamples) continue;
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

        foreach (var grp in todayRecs.GroupBy(r => TimeSlot.SlotOfHour(r.Hour)))
        {
            int total = grp.Count();
            if (total <= 0) continue;
            int intr = grp.Count(r => !string.Equals(r.Result, "PASS", StringComparison.OrdinalIgnoreCase)
                                   && !string.Equals(r.Result, "FAIL", StringComparison.OrdinalIgnoreCase));
            if (intr <= 0) continue;
            double rate = (double)intr / total * 100.0;

            var s = slotInterrupts.FirstOrDefault(x => x.Slot == grp.Key);
            if (s == null || s.SampleCount < minSamples) continue;
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

    private static double Std(List<double> xs)
    {
        if (xs.Count <= 1) return 0;
        var m = xs.Average();
        return Math.Sqrt(xs.Sum(x => (x - m) * (x - m)) / xs.Count);
    }
}
