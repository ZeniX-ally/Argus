using System.Globalization;
using System.Text.RegularExpressions;

namespace FctAggregator;

public static class TimeUtil
{
    private static readonly Regex IsoOffsetRe = new(
        @"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(\.\d+)?\s*([+-]\d{2}:?\d{2}|Z)$",
        RegexOptions.Compiled);

    public static string Normalize(string? ts)
    {
        if (string.IsNullOrWhiteSpace(ts)) return "";
        ts = ts.Trim();
        if (ts.Length == 8 && ts.All(char.IsDigit)) return $"{ts[..4]}-{ts[4..6]}-{ts[6..8]} 00:00:00";
        if (ts.Length == 14 && ts.All(char.IsDigit)) return $"{ts[..4]}-{ts[4..6]}-{ts[6..8]} {ts[8..10]}:{ts[10..12]}:{ts[12..14]}";
        if (ts.Length == 17 && ts.All(char.IsDigit)) return $"{ts[..4]}-{ts[4..6]}-{ts[6..8]} {ts[8..10]}:{ts[10..12]}:{ts[12..14]}";
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
        var name = Path.GetFileNameWithoutExtension(pathOrFileName);
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
