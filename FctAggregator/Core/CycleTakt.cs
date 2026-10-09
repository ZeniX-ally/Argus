namespace FctAggregator;

/// <summary>今日节拍：只统计带总时长的测试。下午平均相对上午平均，正数表示变慢。</summary>
public static class CycleTakt
{
    public readonly struct Summary
    {
        public int Count { get; init; }
        public double? Average { get; init; }
        public int MorningCount { get; init; }
        public double? MorningAvg { get; init; }
        public int AfternoonCount { get; init; }
        public double? AfternoonAvg { get; init; }
        public double? DeltaSeconds { get; init; }
    }

    public static Summary Compute(IEnumerable<(double Seconds, int? Hour)> samples)
    {
        var ok = new List<(double Seconds, int? Hour)>();
        foreach (var s in samples)
        {
            if (double.IsNaN(s.Seconds) || double.IsInfinity(s.Seconds) || s.Seconds <= 0) continue;
            ok.Add(s);
        }
        if (ok.Count == 0) return new Summary();
        var morning = ok.Where(s => s.Hour is >= 0 and <= 11).ToList();
        var afternoon = ok.Where(s => s.Hour is >= 12 and <= 23).ToList();
        double? delta = morning.Count > 0 && afternoon.Count > 0
            ? afternoon.Average(s => s.Seconds) - morning.Average(s => s.Seconds)
            : null;
        return new Summary
        {
            Count = ok.Count,
            Average = ok.Average(s => s.Seconds),
            MorningCount = morning.Count,
            MorningAvg = morning.Count > 0 ? morning.Average(s => s.Seconds) : null,
            AfternoonCount = afternoon.Count,
            AfternoonAvg = afternoon.Count > 0 ? afternoon.Average(s => s.Seconds) : null,
            DeltaSeconds = delta,
        };
    }

    public static string FormatClock(double seconds)
    {
        var s = (int)Math.Round(seconds);
        if (s < 0) s = 0;
        if (s < 60) return $"{s}秒";
        return $"{s / 60}:{s % 60:00}";
    }

    public static string FormatCompare(Summary summary)
    {
        if (summary.DeltaSeconds is not double d) return "";
        var sec = (int)Math.Round(d);
        if (sec == 0) return "与上午持平";
        return sec > 0 ? $"比上午慢 {sec} 秒" : $"比上午快 {-sec} 秒";
    }

    public static string FormatLine(Summary summary)
    {
        if (summary.Count == 0 || summary.Average is not double avg)
            return "今日节拍  —    没有总时长";
        var cmp = FormatCompare(summary);
        var tail = cmp.Length == 0 ? $"{summary.Count} 台有总时长" : $"{cmp} · {summary.Count} 台有总时长";
        return $"今日节拍  {FormatClock(avg)}    {tail}";
    }
}
