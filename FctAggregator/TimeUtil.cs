using System.Globalization;
using System.Text.RegularExpressions;

namespace FctAggregator;

/// <summary>
/// 时间统一解析（唯一入口）。产线 XML 的时间来源有四种、格式各异：
///   · BATCH/PANEL TIMESTAMP：ISO 带时区毫秒  "2026-07-22T12:47:00.213+08:00"
///   · 文件名时间段：17 位数字            "20260722124700283"（yyyyMMddHHmmssfff）
///   · 目录日期：8 位数字                 "20260722"
///   · 程序自身生成：                     "yyyy-MM-dd HH:mm:ss"
/// 统一输出 "yyyy-MM-dd HH:mm:ss"；解析失败返回空串（**不吐原始怪串**，避免界面上直接露 T/毫秒/时区）。
/// ⚠ 时区语义（v3.4.13 加固）：带显式偏移的 ISO 串按**墙上时间**取（偏移前部分），
///   不随运行机器时区转换 —— 产线 XML 的 +08:00 就是测试地的真实时间，工控机时区设置
///   异常（如误设 UTC）时若按本地转换，推送/待办的时间会整体漂移。
/// </summary>
public static class TimeUtil
{
    /// <summary>ISO 带显式偏移：`yyyy-MM-ddTHH:mm:ss[.fff][+08:00|+0800|Z]`。</summary>
    private static readonly Regex IsoOffsetRe = new(
        @"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(\.\d+)?\s*([+-]\d{2}:?\d{2}|Z)$",
        RegexOptions.Compiled);

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
        // ISO 带显式时区偏移：剥掉偏移与毫秒，按墙上时间返回（不随机器时区转换）
        var m = IsoOffsetRe.Match(ts);
        if (m.Success) return Normalize(m.Groups[1].Value.Replace('T', ' '));
        // 其余 ISO（无偏移）等 → 本地时间
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
