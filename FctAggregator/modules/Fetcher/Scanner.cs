using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FctFetcher;

public sealed class FailItem
{
    public string Name = "", Value = "", Lolim = "", Hilim = "", Unit = "";
    /// <summary>步骤类型: NumericLimitTest / PassFailTest / ... 便于排查识别口径</summary>
    public string StepType = "";

    public override string ToString()
        => Value.Length == 0 && Lolim.Length == 0 && Hilim.Length == 0
            ? Name                                        // 无测量值的步骤只显示名字
            : $"{Name}={Value}[{Lolim}~{Hilim}{Unit}]";
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

    /// <summary>供诊断模式调用。</summary>
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

        // BATCH 可能就是根元素本身
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
        if (id.Length > 0) rec.Sn = id;      // DUT/@ID 是权威 SN

        // ---- 失败项识别: 以「叶子失败 GROUP」为测试项主体 ----
        // 为什么不直接遍历 TEST 节点: 若某测试项(GROUP)失败但其下没有 TEST 子节点
        // (例如 PassFailTest / Action 型步骤)，只看 TEST 就会整条漏掉。
        // 以 GROUP 为主体则不依赖 TEST 是否存在；TEST 仅用于补测量值。
        foreach (var g in root.Descendants("GROUP"))
        {
            if ((g.Attribute("STATUS")?.Value ?? "") != "Failed") continue;

            // 内含失败子 GROUP 的是容器(如 MainSequence Callback / SequenceCall)，本身不算测试项
            bool isContainer = g.Elements("GROUP")
                                .Any(x => (x.Attribute("STATUS")?.Value ?? "") == "Failed");
            if (isContainer) continue;

            var gname = g.Attribute("NAME")?.Value ?? "";
            var failedTests = g.Elements("TEST")
                               .Where(t => (t.Attribute("STATUS")?.Value ?? "") == "Failed")
                               .ToList();

            if (failedTests.Count == 0)
            {
                // 没有失败 TEST 子节点 —— 仍记录该测试项，测量值留空
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
                // TEST 的 NAME 通常与 GROUP 同名；缺失时回落到 GROUP 名
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

        // 兜底: 若上面一条都没收到，但文件里确实存在失败的 TEST(说明 GROUP 层级结构
        // 与预期不同)，则退回按 TEST 节点收集，避免整条漏判。
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

        // 只看内容：有真实 fail 项即命中
        rec.Result = rec.FailItems.Count > 0 ? "FAIL" : "NO_FAIL";
    }
}
