using System.Text.RegularExpressions;

namespace FctDistributor;

/// <summary>
/// FCT 结果文件名解析。
/// 格式: {前缀}_Fts_PEU_G49_{机台}_{SN}_{时间戳1}_{时间戳2}.xml
/// 例:   O_Fts_PEU_G49_FCT4_E3002781AFV75236898002K30500274_20260716100234160_20260716681837.xml
/// </summary>
public static class NameParser
{
    private static readonly Regex StationRe = new(@"^FCT\d+$", RegexOptions.Compiled);
    private static readonly Regex ModelRe = new(@"^E\d{7}$", RegexOptions.Compiled);
    public static readonly Regex DateDirRe = new(@"^\d{8}$", RegexOptions.Compiled);

    public static bool IsValidModel(string s) => ModelRe.IsMatch(s);
    public static bool IsDateDir(string s) => DateDirRe.IsMatch(s);

    /// <summary>解析结果。Error 非空表示解析失败。</summary>
    public sealed class Parsed
    {
        public string Prefix = "";       // P / O / F
        public string Station = "";      // FCT4
        public string Sn = "";           // E3002781AFV...
        public string Model = "";        // E3002781 (SN 前 8 位)
        public string Ts1 = "";          // 20260716100234160
        public string Ts2 = "";          // 20260716681837
        public DateTime Stamp;           // Ts1 解析出的时间
        public string? Error;
    }

    public static Parsed Parse(string fileName)
    {
        var r = new Parsed();
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var parts = stem.Split('_');
        if (parts.Length < 4) { r.Error = "文件名段数不足"; return r; }

        r.Prefix = parts[0];

        int si = Array.FindIndex(parts, p => StationRe.IsMatch(p));
        if (si < 0) { r.Error = "文件名中找不到 FCT 机台号"; return r; }
        r.Station = parts[si];

        if (si + 1 >= parts.Length) { r.Error = "机台号后缺少 SN"; return r; }
        r.Sn = parts[si + 1];
        if (r.Sn.Length < 8) { r.Error = $"SN 长度不足 8: {r.Sn}"; return r; }
        r.Model = r.Sn[..8];
        if (!IsValidModel(r.Model)) { r.Error = $"型号不符合 E+7位数字: {r.Model}"; return r; }

        r.Ts1 = parts[^2];
        r.Ts2 = parts[^1];

        var dt = ParseStamp(r.Ts1);
        if (dt == null) { r.Error = $"时间戳无法解析: {r.Ts1}"; return r; }
        r.Stamp = dt.Value;
        return r;
    }

    /// <summary>17 位 yyyyMMddHHmmssfff（或 14 位无毫秒）-> DateTime</summary>
    public static DateTime? ParseStamp(string ts)
    {
        if (ts.Length < 14) return null;
        foreach (var c in ts) if (!char.IsDigit(c)) return null;
        try
        {
            int ms = ts.Length >= 17 ? int.Parse(ts[14..17]) : 0;
            return new DateTime(
                int.Parse(ts[0..4]), int.Parse(ts[4..6]), int.Parse(ts[6..8]),
                int.Parse(ts[8..10]), int.Parse(ts[10..12]), int.Parse(ts[12..14]), ms);
        }
        catch { return null; }
    }

    /// <summary>
    /// 结果分类目录。
    ///
    /// 业务实情：Online 只放 Operator 的 PASS；本工具的两个模板（O_ 中断 / P_）
    /// **只会出现在 Offline**，所以固定为 Offline，不再提供选项。
    /// </summary>
    public const string Category = "Offline";

    /// <summary>目录名是否 Offline（不区分大小写）。</summary>
    public static bool IsOfflineDirName(string s)
        => s.Equals(Category, StringComparison.OrdinalIgnoreCase);
}
