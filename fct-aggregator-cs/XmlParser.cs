using System.Xml;

namespace FctAggregator;

/// <summary>
/// 解析 FCT 的 BATCH 格式 XML。
/// 提取: 时间戳/USER(判debug)/TESTER/PANEL状态/SN/失败步骤明细(排除Get Unit Information)。
/// </summary>
public static class XmlParser
{
    private const string IgnoredFailStep = "Get Unit Information";

    public class ParseResult
    {
        public bool Error { get; set; }
        public string? BatchTimestamp { get; set; }
        public string? FactoryUser { get; set; }
        public string? Tester { get; set; }
        public string? PanelStatus { get; set; }
        public string? Sn { get; set; }
        public string? FailReason { get; set; }
        public bool HasFailItems { get; set; }   // 已排除 Get Unit Information
        public List<FailedTest> FailedTests { get; set; } = new();
        /// <summary>首个 PANEL@TIMESTAMP（真实测试时间；v3.4.12 起作为 batch_timestamp 的优先来源——
        /// BATCH@TIMESTAMP 只是测试 session 打开时间，可能比实际测试早数天）。</summary>
        public string? PanelTimestamp { get; set; }
    }

    /// <summary>深度解析(F_/O_ 用)。失败返回 Error=true。</summary>
    public static ParseResult Parse(string path)
    {
        var r = new ParseResult();
        try
        {
            // 防 XXE: 禁用 DTD
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
                    case "BATCH":
                        r.BatchTimestamp = reader.GetAttribute("TIMESTAMP");
                        break;
                    case "FACTORY":
                        r.FactoryUser = reader.GetAttribute("USER");
                        r.Tester = reader.GetAttribute("TESTER");
                        break;
                    case "PANEL":
                        if (!panelSet)
                        {
                            r.PanelStatus = reader.GetAttribute("STATUS");
                            r.PanelTimestamp = reader.GetAttribute("TIMESTAMP");   // 真实测试时间
                            panelSet = true;
                        }
                        break;
                    case "DUT":
                        if (!snSet)
                        {
                            r.Sn = reader.GetAttribute("ID");
                            snSet = true;
                        }
                        break;
                    case "TEST":
                        if (reader.GetAttribute("STATUS") == "Failed")
                        {
                            var name = reader.GetAttribute("NAME") ?? "";
                            if (name.Contains(IgnoredFailStep)) break; // 排除, 不计入不良
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
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                switch (reader.Name)
                {
                    case "BATCH":
                        d.BatchTimestamp = reader.GetAttribute("TIMESTAMP") ?? "";
                        break;
                    case "FACTORY":
                        d.FactoryUser = reader.GetAttribute("USER") ?? "";
                        d.Tester = reader.GetAttribute("TESTER") ?? "";
                        break;
                    case "PANEL":
                        if (!panelSet) { d.PanelStatus = reader.GetAttribute("STATUS") ?? ""; panelSet = true; }
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
            Logger.Warning($"ParseReport 失败: {path} | {ex.Message}");
            d.Error = true;
        }
        return d;
    }
}
