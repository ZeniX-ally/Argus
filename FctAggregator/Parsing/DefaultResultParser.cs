using System.Collections.Generic;
using System.IO;
using System.Xml;
using FctAggregator;

namespace FctAggregator.Parsing;

/// <summary>
/// 内置默认解析器：完整复刻 v3.4.x 的 FCT BATCH 格式解析（Processor + Classifier + XmlParser 之和），
/// 但所有"硬编码参数"改为读取 <see cref="ParserRuleSet"/>，从而既兼容现有格式、又能被 parsers.json 调参。
///
/// 解析流程（与原 Processor.ParseAndClassify 严格对齐）：
///   1. 路径元信息（category/model/date/prefix/sn/filetime）；
///   2. PASS(P_)：轻量读 USER 判 debug；
///   3. FAIL/INTERRUPTED(F_/O_)：深度解析 XML（时间戳/USER/TESTER/PANEL/SN/失败步骤），
///      排除 IgnoredFailSteps，按前缀+fail 项分类；
///   4. 时间源优先级 PANEL → BATCH → 文件名 → 目录日期；
///   5. 机台号：优先传入 defaultStation，否则从 tester 提取，再否则 UNKNOWN。
/// </summary>
public sealed class DefaultResultParser : IResultParser
{
    private readonly ParserRuleSet _rules;
    private readonly string? _defaultStation;

    public string Id => _rules.Id;
    public int Priority => _rules.Priority;

    public DefaultResultParser(ParserRuleSet rules, string? defaultStation = null)
    {
        _rules = rules;
        _defaultStation = defaultStation;
    }

    public ParseOutput? Parse(string xmlPath, string rawXml)
    {
        if (!xmlPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            return null;

        var info = PathMeta.FromPath(xmlPath, _rules);
        if (info == null)
        {
            return new ParseOutput { Skipped = true, SkipReason = $"路径不匹配规则 {_rules.Id}" };
        }

        // 文件名前缀校验：不在 P_/F_/O_ 等配置前缀里 → 不认识，交给责任链下一个或跳过
        if (!_rules.PrefixResults.ContainsKey(info.Prefix))
        {
            return new ParseOutput { Skipped = true, SkipReason = $"未知文件名前缀 '{info.Prefix}'" };
        }

        var modelName = info.ModelFromName ?? info.Model;
        var snFromName = info.Sn;
        bool isPass = _rules.PrefixResults.TryGetValue(info.Prefix, out var mapped) && mapped == "PASS";

        string result;
        string? user = null, tester = null, panelStatus = null, batchTs = null, failReason = null;
        bool hasFail = false;
        var failedTests = new List<FailedTest>();

        if (isPass)
        {
            user = ReadUserOnly(rawXml);
            if (_rules.IsDebug(user))
                return new ParseOutput { Skipped = true, SkipReason = "debug" };
            result = "PASS";
            panelStatus = "Passed";
        }
        else
        {
            var pr = ParseXml(rawXml);
            if (pr.Error)
            {
                return new ParseOutput { Error = true, SkipReason = "xml 解析异常" };
            }
            if (_rules.IsDebug(pr.FactoryUser))
                return new ParseOutput { Skipped = true, SkipReason = "debug" };

            // 前缀分类（与原 Classifier.ClassifyByPrefix 对齐）：P_=PASS, F_=FAIL, O_=有 fail 项则 FAIL 否则 INTERRUPTED
            result = mapped switch
            {
                "PASS" => "PASS",
                "FAIL" => "FAIL",
                "AUTO" => pr.HasFailItems ? "FAIL" : "INTERRUPTED",
                _ => pr.HasFailItems ? "FAIL" : "INTERRUPTED",
            };
            if (result == "INVALID")
            {
                return new ParseOutput { Error = true, SkipReason = "INVALID 前缀" };
            }
            user = pr.FactoryUser;
            tester = pr.Tester;
            panelStatus = pr.PanelStatus;
            failReason = pr.FailReason;
            hasFail = pr.HasFailItems;
            failedTests = pr.FailedTests;
            if (string.IsNullOrEmpty(snFromName)) snFromName = pr.Sn;
        }

        // 域1 智能时间：文件名时间优先，但用系统时间近邻校验判 SN/误匹配（v3.19.0）。
        // 真正新落盘的 log 文件名时间最贴近系统时间；与系统偏差 >30 天则视为
        // SN/批次号误匹配（TsSegRe 常见陷阱），回退到目录日期。
        var ts = info.FileTime != null
            ? TimeUtil.ResolveFileNameTime(info.FileTime, DateTime.Now)
            : "";
        if (string.IsNullOrEmpty(ts))
            ts = info.TestDate.Length == 8
                ? $"{info.TestDate[..4]}-{info.TestDate[4..6]}-{info.TestDate[6..8]}T00:00:00"
                : "";
        batchTs = ts;

        var stationId = _defaultStation;
        if (string.IsNullOrEmpty(stationId))
            stationId = StationDetector.ExtractStationFromTester(tester) ?? "UNKNOWN";

        long? size = null;
        try { size = new FileInfo(xmlPath).Length; } catch { }

        return new ParseOutput
        {
            Result = result,
            StationId = stationId,
            Model = modelName,
            Category = info.Category,
            TestDate = info.TestDate,
            Sn = snFromName,
            FailReason = failReason,
            Tester = tester,
            PanelStatus = panelStatus,
            BatchTimestamp = batchTs,
            HasFailItems = hasFail,
            FailedTests = failedTests,
            FileSize = size,
        };
    }

    // ===== 以下复刻 XmlParser 的 XML 元素提取，但全部走 _rules 的属性名/排除项 =====

    private sealed class MiniResult
    {
        public bool Error;
        public string? FactoryUser;
        public string? Tester;
        public string? PanelStatus;
        public string? Sn;
        public string? FailReason;
        public bool HasFailItems;
        public List<FailedTest> FailedTests = new();
    }

    private MiniResult ParseXml(string rawXml)
    {
        var r = new MiniResult();
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,   // 防 XXE
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
            };
            using var sr = new StringReader(rawXml);
            using var reader = XmlReader.Create(sr, settings);
            bool snSet = false;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                switch (reader.Name)
                {
                    case "FACTORY":
                        r.FactoryUser = reader.GetAttribute(_rules.AttrFactoryUser);
                        r.Tester = reader.GetAttribute(_rules.AttrTester);
                        break;
                    case "PANEL":
                        r.PanelStatus = reader.GetAttribute(_rules.AttrPanelStatus);
                        break;
                    case "DUT":
                        if (!snSet)
                        {
                            r.Sn = reader.GetAttribute(_rules.AttrDutId);
                            snSet = true;
                        }
                        break;
                    case "TEST":
                        if (reader.GetAttribute(_rules.AttrTestStatus) == "Failed")
                        {
                            var name = reader.GetAttribute(_rules.AttrTestName) ?? "";
                            if (_rules.IgnoredFailSteps.Any(ig => name.Contains(ig))) break; // 排除非真实不良
                            r.HasFailItems = true;
                            r.FailReason ??= name;
                            r.FailedTests.Add(new FailedTest
                            {
                                Name = name,
                                Value = reader.GetAttribute(_rules.AttrTestValue) ?? "",
                                Hilim = reader.GetAttribute(_rules.AttrTestHilim) ?? "",
                                Lolim = reader.GetAttribute(_rules.AttrTestLolim) ?? "",
                                Unit = reader.GetAttribute(_rules.AttrTestUnit) ?? "",
                                Rule = reader.GetAttribute(_rules.AttrTestRule) ?? "",
                            });
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"XML解析失败: {ex.Message}");
            r.Error = true;
        }
        return r;
    }

    private string? ReadUserOnly(string rawXml)
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
            using var sr = new StringReader(rawXml);
            using var reader = XmlReader.Create(sr, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.Name == "FACTORY")
                    return reader.GetAttribute(_rules.AttrFactoryUser) ?? "";
                if (reader.Name == "PANEL") break;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"ReadUserOnly 失败: {ex.Message}");
        }
        return null;
    }
}
