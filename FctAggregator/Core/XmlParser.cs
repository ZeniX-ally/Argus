using System.Text.RegularExpressions;
using System.Xml;

namespace FctAggregator;

/// <summary>
/// 解析 FCT 的 BATCH 格式 XML。
/// 提取: 时间戳/USER(判debug)/TESTER/PANEL状态/SN/失败步骤明细(排除Get Unit Information)。
/// </summary>
public static class XmlParser
{
    /// <summary>排除项（非产品真实不良）：Get Unit Information / UUT Status Err（v3.24.3 起采集口径对齐）。</summary>
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
        /// <summary>治具/机台位 ID（v3.22.0 规格01：FACTORY/PANEL/DUT@FIXTURE_ID，缺省 null）。</summary>
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

    /// <summary>深度解析(F_/O_ 用)。失败返回 Error=true。</summary>
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
                        // v3.22.0 规格01 fixture 归因：FACTORY@FIXTURE_ID（缺省保持 null）
                        r.FixtureId ??= reader.GetAttribute("FIXTURE_ID");
                        break;
                    case "PANEL":
                        // PANEL 也可能携带治具号（部分产线格式）：FACTORY 未命中时兜底
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
                            if (Array.Exists(IgnoredFailSteps, ig => name.Contains(ig))) break; // 排除, 不计入不良
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

    /// <summary>轻量读取: 只取 USER 字段(PASS 文件判 debug 用, 不深度解析)。</summary>
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
                if (reader.Name == "PANEL") break; // FACTORY 在前, 读到 PANEL 还没到就放弃
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"ReadUserOnly 失败: {path} | {ex.Message}");
        }
        return "";
    }

    /// <summary>报告用: 解析头部信息 + 所有 TEST 项(不只 fail)。供 FAIL 详情报告展示。</summary>
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
        public string Status = "";   // Passed / Failed / ...
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

    /// <summary>ParseReport 的文本重载（v3.8.0 在线 XML 查看用：XML 内容来自跨机 HTTP 拉取，
    /// 无本地文件路径）。解析逻辑与文件版完全一致。</summary>
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
