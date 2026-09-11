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
    /// <summary>PASS+FAIL 极值（给「波动最大」排序）。纵轴视野不要用这个——FAIL 掉坑会把 PASS 挤成一条线。</summary>
    public double MinNorm { get; set; }
    public double MaxNorm { get; set; }
    /// <summary>仅 PASS 极值，给纵轴。</summary>
    public double PassMinNorm { get; set; }
    public double PassMaxNorm { get; set; }
    /// <summary>PASS 桶点（实线）。</summary>
    public List<TrendPoint> Points { get; set; } = new();
    /// <summary>FAIL 桶点（虚线，同限值归一化；只有 value 为数字的失败项才进来）。</summary>
    public List<TrendPoint> FailPoints { get; set; } = new();
    /// <summary>FAIL 样本数（桶内计数之和）。</summary>
    public long FailSamples { get; set; }
    public bool HasFail => FailPoints.Count > 0;

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
    /// <summary>FAIL 样本总数（图例/副标题展示用）。</summary>
    public long FailSamplesTotal => Series.Sum(s => s.FailSamples);
    public bool HasAnyFail => Series.Any(s => s.HasFail);
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

    /// <summary>
    /// 构建趋势（X 为 [fromDate, toDateInclusive] 日期闭区间，桶对齐区间起点）。
    /// </summary>
    /// <param name="listNames">
    /// 参与测项清单（<see cref="TrendResult.AllItemNames"/>）的测项；null = 窗口内全部。
    /// UI 右侧勾选列表要用它，所以通常传 null（列全）。
    /// </param>
    /// <param name="bucketNames">
    /// 需要取出时间桶数据的测项；null = 全部。
    /// 只勾选少数测项时传勾选项，可把桶查询从「全部测项 × 桶数」降到「勾选项 × 桶数」（差一两个数量级），
    /// **不影响** <see cref="TrendResult.AllItemNames"/>（清单仍是全量）。
    /// </param>
    public static TrendResult Build(
        Database db, DateTime fromDate, DateTime toDateInclusive, int maxBuckets,
        IEnumerable<string>? listNames = null, IEnumerable<string>? bucketNames = null)
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

        var stats = db.QueryPassTrendItemStats(fromIso, toIso, listNames);
        if (stats.Count == 0)
        {
            var avail = db.GetPassMeasurementDateRange();
            result.Hint = $"窗口 {from:yyyy-MM-dd}~{toExclusive.AddDays(-1):yyyy-MM-dd} 内 test_measurements 无 PASS 测量值" +
                          (avail is { } a
                              ? $"。库内测量实际为 {a.Min}～{a.Max}，把时间范围改到这段，或点「回填历史」补更早数据。"
                              : "。库内尚无测量缓存——点「回填历史」从 XML 补采。");
            return result;
        }
        result.AllItemNames = stats.Select(s => s.TestName).ToList();

        var rows = db.QueryPassTrendBuckets(fromIso, toIso, bucketSeconds, bucketNames);
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

        // FAIL 失败项桶（同区间/同桶宽），按测项名索引后叠到对应曲线上
        var failByName = new Dictionary<string, List<TrendBucketRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in db.QueryFailTrendBuckets(fromIso, toIso, bucketSeconds, bucketNames))
        {
            if (!failByName.TryGetValue(r.TestName, out var flist))
            {
                flist = new List<TrendBucketRow>();
                failByName[r.TestName] = flist;
            }
            flist.Add(r);
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
            double passMin = double.MaxValue, passMax = double.MinValue;
            foreach (var r in bucketRows.OrderBy(x => x.Bucket))
            {
                var norm = Normalize(r.MeanValue, ser.BandLow, ser.BandHigh);
                if (!IsFinite(norm)) continue;
                ser.Points.Add(new TrendPoint { Bucket = r.Bucket, Norm = norm, MeanValue = r.MeanValue, N = r.N });
                if (norm < minN) minN = norm;
                if (norm > maxN) maxN = norm;
                if (norm < passMin) passMin = norm;
                if (norm > passMax) passMax = norm;
            }
            // FAIL 虚线：同测项同限值归一化。极值进 MinNorm（波动排序），不进 PassMin/Max（纵轴）
            if (failByName.TryGetValue(st.TestName, out var failBucketRows))
            {
                foreach (var r in failBucketRows.OrderBy(x => x.Bucket))
                {
                    var norm = Normalize(r.MeanValue, ser.BandLow, ser.BandHigh);
                    if (!IsFinite(norm)) continue;
                    ser.FailPoints.Add(new TrendPoint { Bucket = r.Bucket, Norm = norm, MeanValue = r.MeanValue, N = r.N });
                    ser.FailSamples += r.N;
                    if (norm < minN) minN = norm;
                    if (norm > maxN) maxN = norm;
                }
            }
            if (ser.Points.Count == 0) continue;
            ser.MinNorm = minN;
            ser.MaxNorm = maxN;
            ser.PassMinNorm = passMin;
            ser.PassMaxNorm = passMax;
            result.Series.Add(ser);
        }

        result.Series.Sort((a, b) => string.Compare(a.TestName, b.TestName, StringComparison.OrdinalIgnoreCase));
        result.SkippedNoBand = skipped;
        if (skipped > 0)
            result.Hint = result.Series.Count == 0
                ? $"{skipped} 个测项既无双侧限值、窗口内也无数据量程，无法定位到规格带。"
                : $"另有 {skipped} 个测项无限值且无量程，已跳过。";
        AppendSparseHint(result);
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

    /// <summary>所选窗口远宽于实际有点区间时提示：前段空是缺测量，不是图画坏了。</summary>
    internal static void AppendSparseHint(TrendResult result)
    {
        if (result == null || result.Series.Count == 0 || result.BucketCount < 8) return;
        int minB = int.MaxValue, maxB = int.MinValue;
        foreach (var ser in result.Series)
        {
            foreach (var p in ser.Points)
            {
                if (p.Bucket < minB) minB = p.Bucket;
                if (p.Bucket > maxB) maxB = p.Bucket;
            }
        }
        if (minB == int.MaxValue) return;
        int span = maxB - minB + 1;
        if (span >= result.BucketCount * 0.6) return;
        var a = BucketTime(result.From, minB, result.BucketSeconds);
        var b = BucketTime(result.From, maxB, result.BucketSeconds);
        var extra = $"测量实际落在 {a:yyyy-MM-dd}～{b:yyyy-MM-dd}；所选窗口其余时段无 test_measurements（已清理或未回填），点「回填历史」补采。";
        result.Hint = string.IsNullOrWhiteSpace(result.Hint) ? extra : extra + " " + result.Hint;
    }
}
