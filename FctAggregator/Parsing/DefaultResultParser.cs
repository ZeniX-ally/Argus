using System.Collections.Generic;
using System.IO;
using System.Xml;
using FctAggregator;

namespace FctAggregator.Parsing;

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

        if (!_rules.PrefixResults.ContainsKey(info.Prefix))
        {
            return new ParseOutput { Skipped = true, SkipReason = $"未知文件名前缀 '{info.Prefix}'" };
        }

        var modelName = info.ModelFromName ?? info.Model;
        var snFromName = info.Sn;
        bool isPass = _rules.PrefixResults.TryGetValue(info.Prefix, out var mapped) && mapped == "PASS";

        string result;
        string? user = null, tester = null, panelStatus = null, batchTs = null, failReason = null, fixtureId = null;
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
                return new ParseOutput { Error = true, ErrorCode = "xml_malformed", SkipReason = "xml 解析异常" };
            }
            if (_rules.IsDebug(pr.FactoryUser))
                return new ParseOutput { Skipped = true, SkipReason = "debug" };

            result = mapped switch
            {
                "PASS" => "PASS",
                "FAIL" => "FAIL",
                "AUTO" => pr.HasFailItems ? "FAIL" : "INTERRUPTED",
                _ => pr.HasFailItems ? "FAIL" : "INTERRUPTED",
            };
            if (result == "INVALID")
            {
                return new ParseOutput { Error = true, ErrorCode = "invalid_result", SkipReason = "INVALID 前缀" };
            }
            user = pr.FactoryUser;
            tester = pr.Tester;
            panelStatus = pr.PanelStatus;
            failReason = pr.FailReason;
            hasFail = pr.HasFailItems;
            failedTests = pr.FailedTests;
            fixtureId = pr.FixtureId;
            if (result == "FAIL" && !hasFail && pr.SawIgnoredFail)
                result = "INTERRUPTED";
            if (string.IsNullOrEmpty(snFromName)) snFromName = pr.Sn;
        }

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
            FixtureId = fixtureId,
            BatchTimestamp = batchTs,
            HasFailItems = hasFail,
            FailedTests = failedTests,
            FileSize = size,
        };
    }

    private sealed class MiniResult
    {
        public bool Error;
        public string? FactoryUser;
        public string? Tester;
        public string? FixtureId;
        public string? PanelStatus;
        public string? Sn;
        public string? FailReason;
        public bool HasFailItems;
        public bool SawIgnoredFail;
        public List<FailedTest> FailedTests = new();
    }

    private MiniResult ParseXml(string rawXml)
    {
        var r = new MiniResult();
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
            bool snSet = false;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                switch (reader.Name)
                {
                    case "FACTORY":
                        r.FactoryUser = reader.GetAttribute(_rules.AttrFactoryUser);
                        r.Tester = reader.GetAttribute(_rules.AttrTester);
                        r.FixtureId ??= reader.GetAttribute(_rules.AttrFixtureId);
                        break;
                    case "PANEL":
                        r.FixtureId ??= reader.GetAttribute(_rules.AttrFixtureId);
                        r.PanelStatus = reader.GetAttribute(_rules.AttrPanelStatus);
                        break;
                    case "DUT":
                        if (!snSet)
                        {
                            r.Sn = reader.GetAttribute(_rules.AttrDutId);
                            snSet = true;
                        }
                        r.FixtureId ??= reader.GetAttribute(_rules.AttrFixtureId);
                        break;
                    case "TEST":
                        if (reader.GetAttribute(_rules.AttrTestStatus) == "Failed")
                        {
                            var name = reader.GetAttribute(_rules.AttrTestName) ?? "";
                            if (_rules.IgnoredFailSteps.Any(ig => name.Contains(ig))) { r.SawIgnoredFail = true; break; }
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
