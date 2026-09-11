namespace FctAggregator;

/// <summary>趋势图：测项级归一化基准（限值 + 窗口量程），来自 SQL 聚合。</summary>
public sealed class TrendItemStat
{
    public string TestName { get; set; } = "";
    public double? Lolim { get; set; }
    public double? Hilim { get; set; }
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }
    public string Unit { get; set; } = "";
    public long N { get; set; }
}

/// <summary>趋势图：时间桶聚合行（段内原始均值；归一化在 C# 侧做）。</summary>
public sealed class TrendBucketRow
{
    public string TestName { get; set; } = "";
    public int Bucket { get; set; }
    public double MeanValue { get; set; }
    public long N { get; set; }
}

/// <summary>趋势图：一个时间桶上的点（桶序号 + 段内均值 + 归一化相对位置）。</summary>
public sealed class TrendPoint
{
    public int Bucket { get; set; }
    public double Norm { get; set; }
    public double MeanValue { get; set; }
    public long N { get; set; }
}

/// <summary>趋势图：单测项曲线。BandLow/BandHigh 为归一化 0 与 1 对应的实际值。</summary>
public sealed class TrendSeries
{
    public string TestName { get; set; } = "";
    public string Unit { get; set; } = "";
    public double BandLow { get; set; }
    public double BandHigh { get; set; }
    public bool LowSynthetic { get; set; }
    public bool HighSynthetic { get; set; }
    public long Samples { get; set; }
    public double MinNorm { get; set; }
    public double MaxNorm { get; set; }
    public List<TrendPoint> Points { get; set; } = new();

    /// <summary>单边限（缺失的那一端按窗口数据量程合成）。</summary>
    public bool IsSingleSided => LowSynthetic || HighSynthetic;
}

/// <summary>趋势图：一次构建的完整结果（X=时间桶，Y=相对规格带位置）。</summary>
public sealed class TrendResult
{
    public DateTime From { get; set; }
    public DateTime ToExclusive { get; set; }
    public long BucketSeconds { get; set; }
    public int BucketCount { get; set; }
    public List<string> AllItemNames { get; set; } = new();
    public List<TrendSeries> Series { get; set; } = new();
    public int SkippedNoBand { get; set; }
    public string? Hint { get; set; }

    public bool IsEmpty => Series.Count == 0;
    public int SingleSidedCount => Series.Count(s => s.IsSingleSided);
}

/// <summary>
/// 分析页趋势图构建：把 test_measurements 的 PASS 测量值按时间桶取段内均值，
/// 再用测项限值归一化到「相对规格带位置」（0=下限，1=上限）。
/// 分桶在 SQL 侧做（GROUP BY），C# 只做归一化与纯计算，便于 selftest 直测。
/// </summary>
public static class MeasurementTrendBuilder
{
    /// <summary>nice 桶宽序列（秒）：1/2/5/10/15/30 分、1/2/3/6/12 时、1/2/7/14/30 天。</summary>
    public static readonly long[] BucketSteps =
    {
        60, 120, 300, 600, 900, 1800,
        3600, 7200, 10800, 21600, 43200,
        86400, 172800, 604800, 1209600, 2592000,
    };

    /// <summary>桶宽自适应：取能容纳 span 的最小的 nice 桶宽，使桶数不超过 maxBuckets。</summary>
    public static long ChooseBucketSeconds(TimeSpan span, int maxBuckets)
    {
        maxBuckets = Math.Clamp(maxBuckets, 1, 5000);
        if (span.Ticks <= 0) return BucketSteps[0];
        double total = span.TotalSeconds;
        foreach (var s in BucketSteps)
            if (total / s <= maxBuckets) return s;
        return BucketSteps[^1];
    }

    /// <summary>桶总数（向上取整，至少 1）。</summary>
    public static int CountBuckets(TimeSpan span, long bucketSeconds)
    {
        if (bucketSeconds <= 0 || span.Ticks <= 0) return 1;
        return Math.Max(1, (int)Math.Ceiling(span.TotalSeconds / bucketSeconds));
    }

    /// <summary>把测量值映射到相对规格带位置：0=下限，1=上限。</summary>
    public static double Normalize(double value, double bandLow, double bandHigh)
    {
        if (!(bandHigh > bandLow)) return double.NaN;
        return (value - bandLow) / (bandHigh - bandLow);
    }

    /// <summary>
    /// 解析归一化基准带：双侧限直接用规格带；单边限用窗口量程（max-min）合成缺失的那一端。
    /// 返回 null = 既无限值又无量程（无法定位到规格带，跳过该测项）。
    /// </summary>
    public static (double Low, double High, bool LowSynthetic, bool HighSynthetic)? ResolveBand(
        double? lolim, double? hilim, double? minValue, double? maxValue)
    {
        bool hasLow = lolim.HasValue && IsFinite(lolim.Value);
        bool hasHigh = hilim.HasValue && IsFinite(hilim.Value);
        if (hasLow && hasHigh && hilim!.Value > lolim!.Value)
            return (lolim.Value, hilim.Value, false, false);

        double spread = 0;
        if (minValue.HasValue && maxValue.HasValue && IsFinite(minValue.Value) && IsFinite(maxValue.Value))
            spread = maxValue.Value - minValue.Value;
        if (!(spread > 0)) return null;

        if (hasLow && !hasHigh) return (lolim!.Value, lolim.Value + spread, false, true);
        if (!hasLow && hasHigh) return (hilim!.Value - spread, hilim.Value, true, false);
        // 双侧限退化为零宽（hilim <= lolim）：以该值为中心，用窗口量程撑开参考带
        if (hasLow) return (lolim!.Value - spread / 2, lolim.Value + spread / 2, true, true);
        return (minValue!.Value, maxValue!.Value, true, true);
    }

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    /// <summary>构建趋势（X 为 [fromDate, toDateInclusive] 日期闭区间，桶对齐区间起点）。</summary>
    public static TrendResult Build(
        Database db, DateTime fromDate, DateTime toDateInclusive, int maxBuckets,
        IEnumerable<string>? testNames = null)
    {
        var from = fromDate.Date;
        var toExclusive = toDateInclusive.Date.AddDays(1);
        if (toExclusive <= from) toExclusive = from.AddDays(1);

        var span = toExclusive - from;
        var bucketSeconds = ChooseBucketSeconds(span, maxBuckets);
        var result = new TrendResult
        {
            From = from,
            ToExclusive = toExclusive,
            BucketSeconds = bucketSeconds,
            BucketCount = CountBuckets(span, bucketSeconds),
        };
        if (db == null) return result;

        var fromIso = from.ToString("yyyy-MM-dd HH:mm:ss");
        var toIso = toExclusive.ToString("yyyy-MM-dd HH:mm:ss");

        var stats = db.QueryPassTrendItemStats(fromIso, toIso, testNames);
        if (stats.Count == 0)
        {
            result.Hint = $"窗口 {from:yyyy-MM-dd}~{toExclusive.AddDays(-1):yyyy-MM-dd} 内无 PASS 测量——请调整日期或先回填历史。";
            return result;
        }
        result.AllItemNames = stats.Select(s => s.TestName).ToList();

        var rows = db.QueryPassTrendBuckets(fromIso, toIso, bucketSeconds, testNames);
        var byName = new Dictionary<string, List<TrendBucketRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (!byName.TryGetValue(r.TestName, out var list))
            {
                list = new List<TrendBucketRow>();
                byName[r.TestName] = list;
            }
            list.Add(r);
        }

        int skipped = 0;
        foreach (var st in stats)
        {
            var band = ResolveBand(st.Lolim, st.Hilim, st.MinValue, st.MaxValue);
            if (band == null) { skipped++; continue; }
            if (!byName.TryGetValue(st.TestName, out var bucketRows) || bucketRows.Count == 0) continue;

            var ser = new TrendSeries
            {
                TestName = st.TestName,
                Unit = st.Unit,
                BandLow = band.Value.Low,
                BandHigh = band.Value.High,
                LowSynthetic = band.Value.LowSynthetic,
                HighSynthetic = band.Value.HighSynthetic,
                Samples = st.N,
            };
            double minN = double.MaxValue, maxN = double.MinValue;
            foreach (var r in bucketRows.OrderBy(x => x.Bucket))
            {
                var norm = Normalize(r.MeanValue, ser.BandLow, ser.BandHigh);
                if (!IsFinite(norm)) continue;
                ser.Points.Add(new TrendPoint { Bucket = r.Bucket, Norm = norm, MeanValue = r.MeanValue, N = r.N });
                if (norm < minN) minN = norm;
                if (norm > maxN) maxN = norm;
            }
            if (ser.Points.Count == 0) continue;
            ser.MinNorm = minN;
            ser.MaxNorm = maxN;
            result.Series.Add(ser);
        }

        result.Series.Sort((a, b) => string.Compare(a.TestName, b.TestName, StringComparison.OrdinalIgnoreCase));
        result.SkippedNoBand = skipped;
        if (skipped > 0)
            result.Hint = result.Series.Count == 0
                ? $"{skipped} 个测项既无双侧限值、窗口内也无数据量程，无法定位到规格带。"
                : $"另有 {skipped} 个测项无限值且无量程，已跳过。";
        return result;
    }

    /// <summary>测项名过滤（忽略大小写）；未勾选任何一项则不画线。</summary>
    public static List<TrendSeries> Filter(List<TrendSeries>? series, IEnumerable<string>? names)
    {
        if (series == null || series.Count == 0) return new List<TrendSeries>();
        if (names == null) return new List<TrendSeries>();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
            if (!string.IsNullOrEmpty(n)) set.Add(n);
        if (set.Count == 0) return new List<TrendSeries>();
        return series.Where(s => set.Contains(s.TestName)).ToList();
    }

    /// <summary>测项很多时默认勾选波动最大的若干条（相对规格带内的极差）。</summary>
    public static List<string> TopVolatileNames(List<TrendSeries>? series, int max = 8)
    {
        if (series == null || series.Count == 0) return new List<string>();
        max = Math.Max(1, max);
        var ordered = series.Count <= max
            ? series
            : series.OrderByDescending(s => s.MaxNorm - s.MinNorm)
                    .ThenBy(s => s.TestName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        return ordered.Take(max).Select(s => s.TestName).ToList();
    }

    /// <summary>桶序号 → 时间（桶对齐区间起点）。</summary>
    public static DateTime BucketTime(DateTime from, int bucket, long bucketSeconds)
        => from.AddSeconds((double)bucket * bucketSeconds);

    /// <summary>X 轴刻度文案：桶宽 ≥1 天只到「日期」，否则到「分」。</summary>
    public static string FormatBucketLabel(DateTime from, int bucket, long bucketSeconds)
    {
        var t = BucketTime(from, bucket, bucketSeconds);
        return bucketSeconds >= 86400 ? t.ToString("MM-dd") : t.ToString("MM-dd HH:mm");
    }
}
