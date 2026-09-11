using System.Globalization;
using System.Text.RegularExpressions;

namespace FctAggregator;

public static class TimeUtil
{
    private static readonly Regex IsoOffsetRe = new(
        @"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(\.\d+)?\s*([+-]\d{2}:?\d{2}|Z)$",
        RegexOptions.Compiled);

    /// <summary>FAIL 导出的自然周期口径。</summary>
    public enum NaturalPeriod { Day, Week, Month, Quarter }

    /// <summary>
    /// 自然周期区间（**含首尾**，`today` 由调用方注入以便直测）：
    /// 日=当天；周=本周一至今（ISO 习惯，与现场排班一致）；月=本月 1 号至今；季度=本季度首日（1/4/7/10 月）至今。
    /// </summary>
    public static (DateTime From, DateTime ToInclusive) NaturalRange(NaturalPeriod p, DateTime today)
    {
        var d = today.Date;
        switch (p)
        {
            case NaturalPeriod.Week:
                int dow = ((int)d.DayOfWeek + 6) % 7;   // 周一=0 … 周日=6
                return (d.AddDays(-dow), d);
            case NaturalPeriod.Month:
                return (new DateTime(d.Year, d.Month, 1), d);
            case NaturalPeriod.Quarter:
                int qm = ((d.Month - 1) / 3) * 3 + 1;
                return (new DateTime(d.Year, qm, 1), d);
            default:
                return (d, d);
        }
    }

    /// <summary>自然周期名（导出文件名与菜单用）。</summary>
    public static string NaturalPeriodName(NaturalPeriod p) => p switch
    {
        NaturalPeriod.Week => "本周",
        NaturalPeriod.Month => "本月",
        NaturalPeriod.Quarter => "本季度",
        _ => "今日",
    };

    public static string Normalize(string? ts)
    {
        if (string.IsNullOrWhiteSpace(ts)) return "";
        ts = ts.Trim();
        if (ts.Length == 8 && ts.All(char.IsDigit)) return $"{ts[..4]}-{ts[4..6]}-{ts[6..8]} 00:00:00";
        if (ts.Length == 14 && ts.All(char.IsDigit)) return $"{ts[..4]}-{ts[4..6]}-{ts[6..8]} {ts[8..10]}:{ts[10..12]}:{ts[12..14]}";
        if (ts.Length == 17 && ts.All(char.IsDigit)) return $"{ts[..4]}-{ts[4..6]}-{ts[6..8]} {ts[8..10]}:{ts[10..12]}:{ts[12..14]}";
        // 20260910 08:15:30 / 20260910T08:15:30（目录日+空格时间，TryParse 不认）
        if (ts.Length >= 15 && ts[8] is ' ' or 'T' && ts.Take(8).All(char.IsDigit))
            return Normalize($"{ts[..4]}-{ts[4..6]}-{ts[6..8]} {ts[9..]}");
        var m = IsoOffsetRe.Match(ts);
        if (m.Success) return Normalize(m.Groups[1].Value.Replace('T', ' '));
        if (DateTime.TryParse(ts, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var dt))
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        return "";
    }

    public static string Short(string? ts)
    {
        var n = Normalize(ts);
        if (n.Length == 0) return "—";
        var dt = DateTime.ParseExact(n, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return dt.Date == DateTime.Today ? dt.ToString("今天 HH:mm") : dt.ToString("MM-dd HH:mm");
    }

    private static readonly Regex FileTimeRe =
        new(@"(?:^|_)(\d{14}|\d{17})(?=_|$|[^0-9])", RegexOptions.Compiled);

    public static string ExtractFileNameTime(string? pathOrFileName)
    {
        if (string.IsNullOrWhiteSpace(pathOrFileName)) return "";
        string name;
        try { name = Path.GetFileNameWithoutExtension(pathOrFileName); }
        catch { name = pathOrFileName; }
        var matches = FileTimeRe.Matches(name);
        foreach (Match m in matches)
        {
            var raw = m.Groups[1].Value;
            var s14 = raw.Length >= 14 ? raw[..14] : raw;
            if (DateTime.TryParseExact(s14, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var dt))
            {
                if (dt.Year >= 2020 && dt.Year <= 2099)
                {
                    return dt.ToString("yyyy-MM-dd HH:mm:ss");
                }
            }
        }
        return "";
    }

    public static string ResolveFileNameTime(string? text, DateTime? anchor = null, int maxDaysDiff = 30)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var m = FileTimeRe.Match(text);
        if (!m.Success) return "";
        var norm = Normalize(m.Groups[1].Value);
        if (norm.Length == 0) return "";
        if (anchor.HasValue &&
            DateTime.TryParseExact(norm, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dt))
        {
            var diffDays = Math.Abs((anchor.Value.Date - dt.Date).TotalDays);
            if (diffDays > maxDaysDiff) return "";
        }
        return norm;
    }
}
