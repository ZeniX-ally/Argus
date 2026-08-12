using System.Globalization;

namespace FctAggregator;

/// <summary>
/// 时间统一解析（唯一入口）。产线 XML 的时间来源有四种、格式各异：
///   · BATCH TIMESTAMP：ISO 带时区毫秒  "2026-07-22T12:47:00.213+08:00"
///   · 文件名时间段：17 位数字            "20260722124700283"（yyyyMMddHHmmssfff）
///   · 目录日期：8 位数字                 "20260722"
///   · 程序自身生成：                     "yyyy-MM-dd HH:mm:ss"
/// 统一输出 "yyyy-MM-dd HH:mm:ss"；解析失败返回空串（**不吐原始怪串**，避免界面上直接露 T/毫秒/时区）。
/// </summary>
public static class TimeUtil
{
    /// <summary>统一为 yyyy-MM-dd HH:mm:ss。空 / 无法识别 → ""。</summary>
    public static string Normalize(string? ts)
    {
        if (string.IsNullOrWhiteSpace(ts)) return "";
        ts = ts.Trim();
        // 8 位日期 yyyyMMdd（目录日期）
        if (ts.Length == 8 && ts.All(char.IsDigit)) return $"{ts[..4]}-{ts[4..6]}-{ts[6..8]} 00:00:00";
        // 14 位 yyyyMMddHHmmss
        if (ts.Length == 14 && ts.All(char.IsDigit)) return $"{ts[..4]}-{ts[4..6]}-{ts[6..8]} {ts[8..10]}:{ts[10..12]}:{ts[12..14]}";
        // 17 位 yyyyMMddHHmmssfff（文件名时间，丢毫秒）
        if (ts.Length == 17 && ts.All(char.IsDigit)) return $"{ts[..4]}-{ts[4..6]}-{ts[6..8]} {ts[8..10]}:{ts[10..12]}:{ts[12..14]}";
        // ISO 带时区/毫秒等 → 本地时间
        if (DateTime.TryParse(ts, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var dt))
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        return "";
    }

    /// <summary>短格式: 今天 "今天 HH:mm"，其余 "MM-dd HH:mm"。空 → "—"。</summary>
    public static string Short(string? ts)
    {
        var n = Normalize(ts);
        if (n.Length == 0) return "—";
        var dt = DateTime.ParseExact(n, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return dt.Date == DateTime.Today ? dt.ToString("今天 HH:mm") : dt.ToString("MM-dd HH:mm");
    }
}
