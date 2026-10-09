using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace FctAggregator;

public static class XmlParser
{
    private static readonly string[] IgnoredFailSteps = { "Get Unit Information", "UUT Status Err" };

    public class ParseResult
    {
        public bool Error { get; set; }
        public string? BatchTimestamp { get; set; }
        public string? FactoryUser { get; set; }
        public string? Tester { get; set; }
        public string? PanelStatus { get; set; }
        public string? Sn { get; set; }
        public string? FailReason { get; set; }
        public string? FixtureId { get; set; }
        public bool HasFailItems { get; set; }
        public List<FailedTest> FailedTests { get; set; } = new();
    }

    private static readonly Regex TsSegRe =
        new(@"(?:^|_)(\d{14}|\d{17})(?=_|$)", RegexOptions.Compiled);

    private static string? ExtractFileTime(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var m = TsSegRe.Match(stem);
        if (m.Success) return m.Groups[1].Value;
        return null;
    }

    public static ParseResult Parse(string path)
    {
        var r = new ParseResult();
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
            };
            using var reader = XmlReader.Create(path, settings);
            bool panelSet = false, snSet = false;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                switch (reader.Name)
                {
                    case "FACTORY":
                        r.FactoryUser = reader.GetAttribute("USER");
                        r.Tester = reader.GetAttribute("TESTER");
                        r.FixtureId ??= reader.GetAttribute("FIXTURE_ID");
                        break;
                    case "PANEL":
                        r.FixtureId ??= reader.GetAttribute("FIXTURE_ID");
                        if (!panelSet)
                        {
                            r.PanelStatus = reader.GetAttribute("STATUS");
                            panelSet = true;
                        }
                        break;
                    case "DUT":
                        if (!snSet)
                        {
                            r.Sn = reader.GetAttribute("ID");
                            snSet = true;
                        }
                        r.FixtureId ??= reader.GetAttribute("FIXTURE_ID");
                        break;
                    case "TEST":
                        if (reader.GetAttribute("STATUS") == "Failed")
                        {
                            var name = reader.GetAttribute("NAME") ?? "";
                            if (Array.Exists(IgnoredFailSteps, ig => name.Contains(ig))) break;
                            r.HasFailItems = true;
                            r.FailReason ??= name;
                            r.FailedTests.Add(new FailedTest
                            {
                                Name = name,
                                Value = reader.GetAttribute("VALUE") ?? "",
                                Hilim = reader.GetAttribute("HILIM") ?? "",
                                Lolim = reader.GetAttribute("LOLIM") ?? "",
                                Unit = reader.GetAttribute("UNIT") ?? "",
                                Rule = reader.GetAttribute("RULE") ?? "",
                            });
                        }
                        break;
                }
            }
            var ft = ExtractFileTime(path);
            if (ft != null)
                r.BatchTimestamp = TimeUtil.Normalize(ft);
        }
        catch (Exception ex)
        {
            Logger.Error($"XML解析失败: {path} | {ex.Message}");
            r.Error = true;
        }
        return r;
    }

    public static string ReadUserOnly(string path)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
            };
            using var reader = XmlReader.Create(path, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.Name == "FACTORY")
                    return reader.GetAttribute("USER") ?? "";
                if (reader.Name == "PANEL") break;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"ReadUserOnly 失败: {path} | {ex.Message}");
        }
        return "";
    }

    public class ReportData
    {
        public string BatchTimestamp = "";
        public string FactoryUser = "";
        public string Tester = "";
        public string PanelStatus = "";
        public string Sn = "";
        public List<ReportTest> Tests = new();
        /// <summary>最外层 MainSequence Callback 的 TOTALTIME。没有该节点时为 null。</summary>
        public double? TotalSeconds;
        /// <summary>名称形如「6.1 Power Test」的 SequenceCall，按耗时从高到低。</summary>
        public List<ReportChapter> Chapters = new();
        public bool Error;
    }

    public class ReportChapter
    {
        public string Name = "";
        public double Seconds;
    }

    /// <summary>章节耗时占整次测试的百分比。总时长缺失或不是正数时返回 null。</summary>
    public static double? ChapterShare(double seconds, double? total)
    {
        if (total is not > 0) return null;
        return seconds / total.Value * 100.0;
    }

    public class ReportTest
    {
        public string Name = "";
        public string Value = "";
        public string Lolim = "";
        public string Hilim = "";
        public string Unit = "";
        public string Status = "";
    }

    /// <summary>最外层 MainSequence Callback 的 TOTALTIME。没有则 null。读到即停。</summary>
    public static double? ReadCycleSeconds(string rawXml)
    {
        using var reader = OpenReport(new StringReader(rawXml));
        return ReadCycleSeconds(reader);
    }

    public static double? ReadCycleSecondsFromFile(string path)
    {
        using var reader = OpenReport(path);
        return ReadCycleSeconds(reader);
    }

    private static double? ReadCycleSeconds(XmlReader reader)
    {
        var d = new ReportData();
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "GROUP") continue;
            var type = reader.GetAttribute("TYPE") ?? "";
            if (!type.Equals("SequenceCall", StringComparison.OrdinalIgnoreCase)) continue;
            NoteSequenceCall(reader, d);
            if (d.TotalSeconds != null) return d.TotalSeconds;
        }
        return null;
    }

    public static ReportData ParseReport(string path)
    {
        var d = new ReportData();
        try
        {
            using var reader = OpenReport(path);
            FillReport(reader, d);
            if (d.BatchTimestamp.Length == 0 && ExtractFileTime(path) is { } ft)
                d.BatchTimestamp = TimeUtil.Normalize(ft);
        }
        catch (Exception ex)
        {
            Logger.Warning($"ParseReport 失败: {path} | {ex.Message}");
            d.Error = true;
        }
        return d;
    }

    public static ReportData ParseReportText(string xml, string? filePath = null)
    {
        var d = new ReportData();
        try
        {
            using var reader = OpenReport(new StringReader(xml));
            FillReport(reader, d);
            if (d.BatchTimestamp.Length == 0 && filePath != null && ExtractFileTime(filePath) is { } ft)
                d.BatchTimestamp = TimeUtil.Normalize(ft);
        }
        catch (Exception ex)
        {
            Logger.Warning($"ParseReportText 失败: {ex.Message}");
            d.Error = true;
        }
        return d;
    }

    private static readonly HashSet<string> MeasurementGroupTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "NumericLimitTest", "StringValueTest", "FtsStringValueTest", "PassFailTest",
    };

    /// <summary>章节：一个主步骤号、一个次步骤号，后面是标题。8.1.1.1 这种更深的步骤号不是章节。</summary>
    private static readonly Regex ChapterNameRe = new(@"^\d+\.\d+\s+\S", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static XmlReader OpenReport(string path) =>
        XmlReader.Create(path, ReportSettings());

    private static XmlReader OpenReport(TextReader text) =>
        XmlReader.Create(text, ReportSettings());

    private static XmlReaderSettings ReportSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
    };

    /// <summary>
    /// 测项来源有两处：带测量值的 <c>TEST</c>，以及没有子 <c>TEST</c> 的测量 <c>GROUP</c>
    /// （Skipped 的 NumericLimitTest 等是空元素，原来整项被丢掉）。
    /// </summary>
    private static void FillReport(XmlReader reader, ReportData d)
    {
        bool panelSet = false, snSet = false;
        var pending = new Stack<(string Name, string Status, bool GotTest)>();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "GROUP")
            {
                if (pending.Count > 0)
                {
                    var top = pending.Pop();
                    if (!top.GotTest)
                        d.Tests.Add(new ReportTest { Name = top.Name, Status = top.Status });
                }
                continue;
            }
            if (reader.NodeType != XmlNodeType.Element) continue;
            switch (reader.Name)
            {
                case "BATCH":
                    if (d.BatchTimestamp.Length == 0)
                        d.BatchTimestamp = TimeUtil.Normalize(reader.GetAttribute("TIMESTAMP") ?? "");
                    break;
                case "FACTORY":
                    d.FactoryUser = reader.GetAttribute("USER") ?? "";
                    d.Tester = reader.GetAttribute("TESTER") ?? "";
                    break;
                case "PANEL":
                    if (!panelSet)
                    {
                        d.PanelStatus = reader.GetAttribute("STATUS") ?? "";
                        var pt = reader.GetAttribute("TIMESTAMP") ?? "";
                        if (pt.Length > 0) d.BatchTimestamp = TimeUtil.Normalize(pt);
                        panelSet = true;
                    }
                    break;
                case "DUT":
                    if (!snSet) { d.Sn = reader.GetAttribute("ID") ?? ""; snSet = true; }
                    break;
                case "GROUP":
                    var type = reader.GetAttribute("TYPE") ?? "";
                    if (type.Equals("SequenceCall", StringComparison.OrdinalIgnoreCase))
                        NoteSequenceCall(reader, d);
                    if (!MeasurementGroupTypes.Contains(type)) break;
                    var gname = reader.GetAttribute("NAME") ?? "";
                    var gstatus = reader.GetAttribute("STATUS") ?? "";
                    if (reader.IsEmptyElement)
                        d.Tests.Add(new ReportTest { Name = gname, Status = gstatus });
                    else
                        pending.Push((gname, gstatus, false));
                    break;
                case "TEST":
                    d.Tests.Add(new ReportTest
                    {
                        Name = reader.GetAttribute("NAME") ?? "",
                        Value = reader.GetAttribute("VALUE") ?? "",
                        Lolim = reader.GetAttribute("LOLIM") ?? "",
                        Hilim = reader.GetAttribute("HILIM") ?? "",
                        Unit = reader.GetAttribute("UNIT") ?? "",
                        Status = reader.GetAttribute("STATUS") ?? "",
                    });
                    if (pending.Count > 0)
                    {
                        var top = pending.Pop();
                        pending.Push((top.Name, top.Status, true));
                    }
                    break;
            }
        }
        if (d.Chapters.Count > 1)
            d.Chapters = d.Chapters.OrderByDescending(c => c.Seconds).ToList();
    }

    private static void NoteSequenceCall(XmlReader reader, ReportData d)
    {
        var raw = reader.GetAttribute("TOTALTIME");
        if (string.IsNullOrWhiteSpace(raw)) return;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)
            || double.IsNaN(sec) || double.IsInfinity(sec) || sec < 0)
            return;
        var name = (reader.GetAttribute("NAME") ?? "").Trim();
        if (name.Equals("MainSequence Callback", StringComparison.OrdinalIgnoreCase))
        {
            if (d.TotalSeconds == null) d.TotalSeconds = sec;
            return;
        }
        if (!ChapterNameRe.IsMatch(name)) return;
        d.Chapters.Add(new ReportChapter { Name = name, Seconds = sec });
    }
}
