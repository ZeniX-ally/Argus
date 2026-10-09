using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FctFetcher;

public sealed class FailItem
{
    public string Name = "", Value = "", Lolim = "", Hilim = "", Unit = "";
    public string StepType = "";

    public override string ToString()
        => Value.Length == 0 && Lolim.Length == 0 && Hilim.Length == 0
            ? Name
            : $"{Name}={Value}[{Lolim}~{Hilim}{Unit}]";
}

public sealed class Record
{
    public string XmlPath = "", Filename = "", Category = "", Model = "", Date = "";
    public string Prefix = "", Sn = "", Station = "", User = "", Timestamp = "";
    public string Result = "";
    public List<FailItem> FailItems = new();
    public string CsvPath = "";
    public List<string> TdmsPaths = new();
}

public sealed class ScanStats
{
    public int XmlTotal, SkipBadPath, SkipRange, InRange;
    public int SkipNoFail, SkipDebug, SkipParseError, Fail;
}

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

    public static void ParseXmlPublic(Record rec, bool excludeIgnored) => ParseXml(rec, excludeIgnored);

    private static void ParseXml(Record rec, bool excludeIgnored)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(rec.XmlPath, LoadOptions.None);
        }
        catch
        {
            rec.Result = "PARSE_ERROR";
            return;
        }

        var root = doc.Root;
        if (root == null) { rec.Result = "PARSE_ERROR"; return; }

        var batch = root.Name.LocalName == "BATCH" ? root : root.Descendants("BATCH").FirstOrDefault();
        if (batch != null) rec.Timestamp = batch.Attribute("TIMESTAMP")?.Value ?? "";

        var fac = root.Descendants("FACTORY").FirstOrDefault();
        if (fac != null)
        {
            rec.User = fac.Attribute("USER")?.Value ?? "";
            var tester = fac.Attribute("TESTER")?.Value ?? "";
            if (rec.Station.Length == 0 && tester.Length > 0)
            {
                var m = TesterStationRe.Match(tester);
                if (m.Success) rec.Station = m.Value;
            }
        }

        var dut = root.Descendants("DUT").FirstOrDefault();
        var id = dut?.Attribute("ID")?.Value ?? "";
        if (id.Length > 0) rec.Sn = id;

        foreach (var g in root.Descendants("GROUP"))
        {
            if ((g.Attribute("STATUS")?.Value ?? "") != "Failed") continue;

            bool isContainer = g.Elements("GROUP")
                                .Any(x => (x.Attribute("STATUS")?.Value ?? "") == "Failed");
            if (isContainer) continue;

            var gname = g.Attribute("NAME")?.Value ?? "";
            var failedTests = g.Elements("TEST")
                               .Where(t => (t.Attribute("STATUS")?.Value ?? "") == "Failed")
                               .ToList();

            if (failedTests.Count == 0)
            {
                var name = gname;
                if (excludeIgnored && IsIgnored(name)) continue;
                rec.FailItems.Add(new FailItem
                {
                    Name = name,
                    StepType = g.Attribute("TYPE")?.Value ?? "",
                });
                continue;
            }

            foreach (var t in failedTests)
            {
                var name = t.Attribute("NAME")?.Value;
                if (string.IsNullOrWhiteSpace(name)) name = gname;
                if (excludeIgnored && IsIgnored(name)) continue;
                rec.FailItems.Add(new FailItem
                {
                    Name = name,
                    Value = t.Attribute("VALUE")?.Value ?? "",
                    Lolim = t.Attribute("LOLIM")?.Value ?? "",
                    Hilim = t.Attribute("HILIM")?.Value ?? "",
                    Unit = t.Attribute("UNIT")?.Value ?? "",
                    StepType = g.Attribute("TYPE")?.Value ?? "",
                });
            }
        }

        if (rec.FailItems.Count == 0)
        {
            foreach (var t in root.Descendants("TEST"))
            {
                if ((t.Attribute("STATUS")?.Value ?? "") != "Failed") continue;
                var name = t.Attribute("NAME")?.Value ?? "";
                if (excludeIgnored && IsIgnored(name)) continue;
                rec.FailItems.Add(new FailItem
                {
                    Name = name,
                    Value = t.Attribute("VALUE")?.Value ?? "",
                    Lolim = t.Attribute("LOLIM")?.Value ?? "",
                    Hilim = t.Attribute("HILIM")?.Value ?? "",
                    Unit = t.Attribute("UNIT")?.Value ?? "",
                    StepType = "(fallback:TEST)",
                });
            }
        }

        rec.Result = rec.FailItems.Count > 0 ? "FAIL" : "NO_FAIL";
    }
}
