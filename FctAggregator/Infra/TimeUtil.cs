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

    /// <summary>
    /// 文件名时间戳正则：匹配下划线分割段中的纯 14/17 位数字（yyyyMMddHHmmss[fff]）。
    /// ⚠ 这段纯数字也可能是 SN/批次号被误当时间，因此只作「原始提取」，
    ///    是否可信由 <see cref="ResolveFileNameTime"/> 用系统时间近邻校验决定，
    ///    不要单独拿正则匹配结果直接当测试时间。
    /// </summary>
    private static readonly Regex FileTimeRe =
        new(@"(?:^|_)(\d{14}|\d{17})(?=_|$|[^0-9])", RegexOptions.Compiled);

    /// <summary>
    /// 从 log 文件路径或文件名中提取测试时间（精确到秒：yyyy-MM-dd HH:mm:ss）。
    /// 遍历匹配 17 位 (yyyyMMddHHmmssfff) 或 14 位 (yyyyMMddHHmmss) 的合法时间戳段，
    /// 严格校验年(2020~2099)、月(1~12)、日(1~31)、时(0~23)、分(0~59)、秒(0~59)。
    /// 遇到第一个合法的时间段即返回，避免误把后面的流水号段识别为时间。未找到有效时间段返回空字符串。
    /// </summary>
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

    /// <summary>
    /// 文件名时间戳提取 + 系统时间近邻校验（域1 智能时间，v3.19.0）。
    /// 真正新落盘的测试 log 其文件名时间一定最贴近系统当前时间，而 SN/批次号等纯数字
    /// 段与系统时间偏差巨大 —— 所以用 <paramref name="anchor"/>（系统当前时间）做锚：
    ///   · 提取文件名 14/17 位候选；
    ///   · 候选与锚的绝对天数差 &gt; <paramref name="maxDaysDiff"/>（默认 30）→ 判为
    ///     SN/历史误匹配，返回 ""（调用方回退目录日期 <c>yyyyMMdd</c> 等兜底源）。
    /// 返回归一化 yyyy-MM-dd HH:mm:ss；无匹配 / 判为 SN / 解析失败 → ""。
    /// </summary>
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
            if (diffDays > maxDaysDiff) return "";   // 与系统时间偏差过大 → SN/误匹配，回退
        }
        return norm;
    }
}
