using System.Globalization;

namespace FctAggregator;

/// <summary>同一序列号的前后测试：当日第几次、上一次、失败项相对上次是出限还是回到限内、章节耗时差。</summary>
public static class UnitRetest
{
    public enum Move
    {
        None,
        Out,
        Further,
        Closer,
        BackIn,
        Flat,
        StillIn,
    }

    public sealed class Place
    {
        public bool Found;
        public int IndexOnDay;
        public int CountOnDay;
        public UnitRun? Previous;
        public UnitRun? Current;
    }

    public static string DayKey(string? timestamp, string? testDate)
    {
        var ts = timestamp ?? "";
        if (ts.Length >= 10 && ts[4] == '-' && ts[7] == '-')
            return ts[..10];
        var digits = (testDate ?? "").Replace("-", "");
        if (digits.Length >= 8 && digits[..8].All(char.IsDigit))
            return $"{digits[..4]}-{digits[4..6]}-{digits[6..8]}";
        return "";
    }

    public static Place Locate(IReadOnlyList<UnitRun> ordered, string currentPath)
    {
        var place = new Place();
        if (ordered.Count == 0 || string.IsNullOrWhiteSpace(currentPath)) return place;
        int at = -1;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (string.Equals(ordered[i].XmlPath, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                at = i;
                break;
            }
        }
        if (at < 0) return place;
        place.Found = true;
        place.Current = ordered[at];
        if (at > 0) place.Previous = ordered[at - 1];
        var day = DayKey(ordered[at].Timestamp, ordered[at].TestDate);
        int index = 0, count = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (DayKey(ordered[i].Timestamp, ordered[i].TestDate) != day) continue;
            count++;
            if (i <= at) index++;
        }
        place.IndexOnDay = index;
        place.CountOnDay = count;
        return place;
    }

    public static string FormatOrdinal(Place place) =>
        place.Found ? $"当日第 {place.IndexOnDay}/{place.CountOnDay} 次" : "";

    public static string FormatPrevious(UnitRun previous, string currentDayKey)
    {
        var ts = previous.Timestamp ?? "";
        var clock = ts.Length >= 16 ? ts.Substring(11, 5).Replace('T', ' ') : ts;
        var day = DayKey(previous.Timestamp, previous.TestDate);
        if (day.Length == 10 && !string.Equals(day, currentDayKey, StringComparison.Ordinal))
            clock = day[5..] + " " + clock;
        var result = previous.Result ?? "";
        if (result.Equals("FAIL", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(previous.FailReason))
            return $"上次 {clock} FAIL · {previous.FailReason}".Trim();
        return $"上次 {clock} {result}".Trim();
    }

    public static Move Judge(double? current, double? previous, double? lo, double? hi)
    {
        if (current == null || previous == null) return Move.None;
        if (lo == null && hi == null) return Move.None;
        if (lo is double l && hi is double h && l > h) return Move.None;
        bool curOut = IsOut(current.Value, lo, hi);
        bool prevOut = IsOut(previous.Value, lo, hi);
        if (!curOut && !prevOut) return Move.StillIn;
        if (!prevOut && curOut) return Move.Out;
        if (prevOut && !curOut) return Move.BackIn;
        var cd = OutsideDistance(current.Value, lo, hi);
        var pd = OutsideDistance(previous.Value, lo, hi);
        if (Math.Abs(cd - pd) < 1e-9) return Move.Flat;
        return cd > pd ? Move.Further : Move.Closer;
    }

    public static string FormatItemShift(string? priorText, double? priorValue, Move move)
    {
        var shown = !string.IsNullOrWhiteSpace(priorText)
            ? priorText.Trim()
            : priorValue is double v ? v.ToString("0.####", CultureInfo.InvariantCulture) : "";
        if (shown.Length == 0) return "";
        var label = move switch
        {
            Move.Out => "出限",
            Move.Further => "更出限",
            Move.Closer => "靠近限内",
            Move.BackIn => "回到限内",
            Move.Flat => "持平",
            Move.StillIn => "仍在限内",
            _ => "",
        };
        return label.Length == 0 ? $"上次 {shown}" : $"上次 {shown} · {label}";
    }

    public static double? PriorChapterSeconds(IEnumerable<XmlParser.ReportChapter> prior, string name)
    {
        foreach (var c in prior)
        {
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                return c.Seconds;
        }
        return null;
    }

    public static string FormatDelta(double current, double prior)
    {
        var d = current - prior;
        var sign = d > 0 ? "+" : "";
        return $"{sign}{d.ToString("0.0", CultureInfo.InvariantCulture)} 秒";
    }

    public static bool TryNum(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool IsOut(double v, double? lo, double? hi)
    {
        if (lo is double l && v < l) return true;
        if (hi is double h && v > h) return true;
        return false;
    }

    private static double OutsideDistance(double v, double? lo, double? hi)
    {
        if (lo is double l && v < l) return l - v;
        if (hi is double h && v > h) return v - h;
        return 0;
    }
}

public sealed class UnitRun
{
    public string XmlPath = "";
    public string Result = "";
    public string FailReason = "";
    public string Timestamp = "";
    public string TestDate = "";
}

public sealed class UnitItemPrior
{
    public double? Value;
    public string ValueText = "";
    public double? Lo;
    public double? Hi;
}
