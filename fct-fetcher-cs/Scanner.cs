using System.Text.RegularExpressions;
using System.Xml;

namespace FctFetcher;

public sealed class FailItem
{
    public string Name = "", Value = "", Lolim = "", Hilim = "", Unit = "";
    public override string ToString()
        => $"{Name}={Value}[{Lolim}~{Hilim}{Unit}]";
}

public sealed class Record
{
    public string XmlPath = "", Filename = "", Category = "", Model = "", Date = "";
    public string Prefix = "", Sn = "", Station = "", User = "", Timestamp = "";
    public string Result = "";                       // FAIL / NO_FAIL / PARSE_ERROR
    public List<FailItem> FailItems = new();
    public string CsvPath = "";
    public List<string> TdmsPaths = new();
}

public sealed class ScanStats
{
    public int XmlTotal, SkipBadPath, SkipRange, InRange;
    public int SkipNoFail, SkipDebug, SkipParseError, Fail;
}

/// <summary>
/// 扫描 Results 并判定命中。
///
/// 判定规则（按生产环境实际）:
///   只扫 Offline —— 生产环境 Online 全是 pass，没必要扫
///   只看 XML 内容: 解析到含真实 fail 测试项即判定命中，不依赖文件名前缀
///   真实 fail 项 = TEST 节点且 STATUS="Failed"，默认排除
///     "Get Unit Information" / "UUT Status Err"（非产品真实不良）
///   FACTORY/@USER == debug 跳过
///   SN 以 DUT/@ID 为准（权威），文件名仅兜底
/// </summary>
public static class Scanner
{
    private static readonly Regex ModelRe = new(@"^E\d{7}$", RegexOptions.Compiled);
    private static readonly Regex DateRe = new(@"^\d{8}$", RegexOptions.Compiled);
    private static readonly Regex StationRe = new(@"^FCT\d+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TesterStationRe = new(@"FCT\d+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] IgnoredFailSteps = { "Get Unit Information", "UUT Status Err" };

    private static bool IsIgnored(string name)
    {
        foreach (var ig in IgnoredFailSteps)
            if (name.Trim().Equals(ig, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static List<Record> Scan(Config cfg, DateTime start, DateTime end,
                                    out ScanStats stats, Action<string>? log = null,
                                    Action<int, int>? progress = null)
    {
        stats = new ScanStats();
        var recs = new List<Record>();

        if (!Directory.Exists(cfg.ResultsRoot))
        {
            log?.Invoke($"[错误] Results 目录不存在: {cfg.ResultsRoot}");
            return recs;
        }

        // 只枚举需要的分类子目录，不做全盘 rglob —— 生产环境 Online 数据量大且全是 pass
        var files = new List<string>();
        foreach (var cat in cfg.Categories)
        {
            var dir = Path.Combine(cfg.ResultsRoot, cat);
            if (!Directory.Exists(dir))
            {
                log?.Invoke($"[提示] 分类目录不存在，跳过: {dir}");
                continue;
            }
            files.AddRange(Directory.EnumerateFiles(dir, "*.xml", SearchOption.AllDirectories));
        }
        stats.XmlTotal = files.Count;
        log?.Invoke($"扫描 {cfg.ResultsRoot} [{string.Join(", ", cfg.Categories)}] -> {files.Count} 个 XML");

        int done = 0;
        foreach (var f in files)
        {
            if (++done % 200 == 0) progress?.Invoke(done, files.Count);

            var rec = ParsePath(f, cfg.Categories);
            if (rec == null) { stats.SkipBadPath++; continue; }

            if (!DateTime.TryParseExact(rec.Date, "yyyyMMdd", null,
                    System.Globalization.DateTimeStyles.None, out var d))
            { stats.SkipBadPath++; continue; }
            if (d < start.Date || d > end.Date) { stats.SkipRange++; continue; }
            stats.InRange++;

            ParseXml(rec, cfg.ExcludeIgnoredSteps);

            if (rec.Result == "PARSE_ERROR") { stats.SkipParseError++; continue; }
            if (cfg.SkipDebug && rec.User.Trim().Equals("debug", StringComparison.OrdinalIgnoreCase))
            { stats.SkipDebug++; continue; }
            if (rec.FailItems.Count == 0) { stats.SkipNoFail++; continue; }

            recs.Add(rec);
            stats.Fail++;
        }
        progress?.Invoke(files.Count, files.Count);
        return recs;
    }

    /// <summary>路径须为 {Category}\{型号E+7位}\{yyyyMMdd}\{文件名}。目录名大小写不敏感。</summary>
    public static Record? ParsePath(string path, string[] categories)
    {
        var parts = path.Replace('/', '\\').Split('\\');
        if (parts.Length < 4) return null;

        string filename = parts[^1], date = parts[^2], model = parts[^3], category = parts[^4];

        string? canon = categories.FirstOrDefault(
            c => c.Equals(category, StringComparison.OrdinalIgnoreCase));
        if (canon == null) return null;
        if (!ModelRe.IsMatch(model)) return null;
        if (!DateRe.IsMatch(date)) return null;

        var rec = new Record
        {
            XmlPath = path,
            Filename = filename,
            Category = canon,
            Model = model,
            Date = date,
            // 前缀仅作展示信息保留，不参与判定
            Prefix = filename.Length >= 2 ? filename[..2].ToUpperInvariant() : "",
        };

        var stem = Path.GetFileNameWithoutExtension(filename)
                       .Replace("(debug)", "").Replace("(Debug)", "");
        var seg = stem.Split('_');
        int si = Array.FindIndex(seg, s => StationRe.IsMatch(s));
        if (si >= 0)
        {
            rec.Station = seg[si];
            if (si + 1 < seg.Length) rec.Sn = seg[si + 1];
        }
        else if (seg.Length >= 6) rec.Sn = seg[5];

        return rec;
    }

    private static void ParseXml(Record rec, bool excludeIgnored)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };
        try
        {
            using var reader = XmlReader.Create(rec.XmlPath, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                switch (reader.Name)
                {
                    case "BATCH":
                        rec.Timestamp = reader.GetAttribute("TIMESTAMP") ?? "";
                        break;
                    case "FACTORY":
                        rec.User = reader.GetAttribute("USER") ?? "";
                        var tester = reader.GetAttribute("TESTER") ?? "";
                        if (rec.Station.Length == 0 && tester.Length > 0)
                        {
                            var m = TesterStationRe.Match(tester);
                            if (m.Success) rec.Station = m.Value;
                        }
                        break;
                    case "DUT":
                        // DUT/@ID 是权威 SN（文件名里的可能被截断）
                        var id = reader.GetAttribute("ID") ?? "";
                        if (id.Length > 0) rec.Sn = id;
                        break;
                    case "TEST":
                        if (reader.GetAttribute("STATUS") == "Failed")
                        {
                            var name = reader.GetAttribute("NAME") ?? "";
                            if (excludeIgnored && IsIgnored(name)) break;
                            rec.FailItems.Add(new FailItem
                            {
                                Name = name,
                                Value = reader.GetAttribute("VALUE") ?? "",
                                Lolim = reader.GetAttribute("LOLIM") ?? "",
                                Hilim = reader.GetAttribute("HILIM") ?? "",
                                Unit = reader.GetAttribute("UNIT") ?? "",
                            });
                        }
                        break;
                }
            }
        }
        catch
        {
            rec.Result = "PARSE_ERROR";
            return;
        }

        // 只看内容：有真实 fail 项即命中
        rec.Result = rec.FailItems.Count > 0 ? "FAIL" : "NO_FAIL";
    }
}
