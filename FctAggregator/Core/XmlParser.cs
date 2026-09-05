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
        public bool Error;
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

    public static ReportData ParseReport(string path)
    {
        var d = new ReportData();
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
            var ft = ExtractFileTime(path);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                switch (reader.Name)
                {
                    case "FACTORY":
                        d.FactoryUser = reader.GetAttribute("USER") ?? "";
                        d.Tester = reader.GetAttribute("TESTER") ?? "";
                        break;
                    case "PANEL":
                        if (!panelSet)
                        {
                            d.PanelStatus = reader.GetAttribute("STATUS") ?? "";
                            panelSet = true;
                        }
                        break;
                    case "DUT":
                        if (!snSet) { d.Sn = reader.GetAttribute("ID") ?? ""; snSet = true; }
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
                        break;
                }
            }
            if (ft != null)
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
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
            };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            bool panelSet = false, snSet = false;
            var ft = filePath != null ? ExtractFileTime(filePath) : null;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                switch (reader.Name)
                {
                    case "FACTORY":
                        d.FactoryUser = reader.GetAttribute("USER") ?? "";
                        d.Tester = reader.GetAttribute("TESTER") ?? "";
                        break;
                    case "PANEL":
                        if (!panelSet)
                        {
                            d.PanelStatus = reader.GetAttribute("STATUS") ?? "";
                            panelSet = true;
                        }
                        break;
                    case "DUT":
                        if (!snSet) { d.Sn = reader.GetAttribute("ID") ?? ""; snSet = true; }
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
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"ParseReportText 失败: {ex.Message}");
            d.Error = true;
        }
        return d;
    }
}
