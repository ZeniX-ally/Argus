using System.Collections.Generic;
using System.Globalization;
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
        // 审计修复：prefix_results 的值来自用户 JSON，原来只做大小写敏感匹配——写成 "pass" 时
        // PASS 文件会走 FAIL 分支被记成 INTERRUPTED（良率静默归零）。
        bool isPass = _rules.PrefixResults.TryGetValue(info.Prefix, out var mapped)
                      && string.Equals(mapped, "PASS", StringComparison.OrdinalIgnoreCase);

        string result;
        string? user = null, tester = null, panelStatus = null, batchTs = null, failReason = null, fixtureId = null;
        bool hasFail = false;
        var failedTests = new List<FailedTest>();
        var measurements = new List<MeasurementRow>();

        if (isPass)
        {
            // 审计 A5：PASS 分支此前不校验 XML 完整性，ReadFactoryOnly 吞异常会把截断/损坏文件静默入库为 PASS，污染良率
            try
            {
                using var xr = System.Xml.XmlReader.Create(new StringReader(rawXml),
                    new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
                while (xr.Read()) { }
            }
            catch (Exception ex)
            {
                return new ParseOutput { Error = true, ErrorCode = "xml_malformed", SkipReason = $"PASS 文件 XML 校验失败: {ex.Message}" };
            }
            var fac = ReadFactoryOnly(rawXml);
            user = fac.user;
            tester = fac.tester;
            fixtureId = fac.fixtureId;
            if (_rules.IsDebug(user))
                return new ParseOutput { Skipped = true, SkipReason = "debug" };
            if (string.IsNullOrEmpty(snFromName)) snFromName = fac.sn;
            result = "PASS";
            panelStatus = "Passed";
            measurements = ParseMeasurements(rawXml);
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

            // 审计修复：原 switch 只匹配大写，且没有 "INVALID" 分支——parsers.json 里写 "I_":"INVALID"
            // 会被静默记成 FAIL/INTERRUPTED（下面的 INVALID 保护永远不可达）。这里统一大写归一后再判定。
            var mappedU = (mapped ?? "").Trim().ToUpperInvariant();
            result = mappedU switch
            {
                "PASS" => "PASS",
                "FAIL" => "FAIL",
                "INVALID" => "INVALID",
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

        // anchor 用**目录日期**而非"现在"：回填历史 XML（>30 天）时文件名时间会被 anchor 判为不可信而丢弃，
        // 与 v3.40.1 的 90/365 天回填口径矛盾（ALGO-1 日期口径）。目录日是该文件"应有的那天"，
        // 用它做锚既能容跨零点文件，又能挡住明显伪造的远日期段。
        var anchor = DateTime.TryParseExact(info.TestDate, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dirDay) ? dirDay : DateTime.Now;
        var ts = info.FileTime != null
            ? TimeUtil.ResolveFileNameTime(info.FileTime, anchor)
            : "";
        if (string.IsNullOrEmpty(ts))
            ts = info.TestDate.Length == 8
                ? $"{info.TestDate[..4]}-{info.TestDate[4..6]}-{info.TestDate[6..8]}T00:00:00"
                : "";
        batchTs = ts;

        // 站号回落链：实例值（Load 传入的 config.station_id）→ 注册表注入值（Engine 的 IP 识别结果）
        // → TESTER 属性解析 → UNKNOWN。审计修复：中间一环此前缺失——IP 识别出的机台号进不来，
        // 整库 station_id 落 UNKNOWN，Engine 的 [跳过推送-无机台号] 会把真 FAIL 告警静默吞掉。
        var stationId = _defaultStation;
        if (string.IsNullOrEmpty(stationId)) stationId = ParserRegistry.DefaultStation;
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
            Measurements = measurements,
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

    /// <summary>规格06：PASS 文件测量值单遍流式提取（TEST 全收不限 Failed，护栏可关）。</summary>
    internal List<MeasurementRow> ParseMeasurements(string rawXml)
    {
        var rows = new List<MeasurementRow>();
        var cfg = AppConfig.Instance;
        if (!cfg.AnalyzeCollectPass) return rows;
        if (rawXml.Length > (long)cfg.AnalyzeMaxFileKb * 1024)
        {
            Logger.Warning($"[分析采集] 文件超大({rawXml.Length / 1024}KB>{cfg.AnalyzeMaxFileKb}KB)跳过测量值提取");
            return rows;
        }
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
                if (reader.NodeType != XmlNodeType.Element || reader.Name != "TEST") continue;
                var name = reader.GetAttribute(_rules.AttrTestName);
                if (string.IsNullOrEmpty(name)) continue;
                if (rows.Count >= cfg.AnalyzeMaxTestsPerFile)
                {
                    Logger.Warning($"[分析采集] 单文件测项超上限({cfg.AnalyzeMaxTestsPerFile})截断: {name}");
                    break;
                }
                var raw = reader.GetAttribute(_rules.AttrTestValue);
                double? num = null;
                if (!string.IsNullOrWhiteSpace(raw) &&
                    double.TryParse(raw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var d))
                    num = d;
                double? ParseLim(string attr)
                {
                    var s = reader.GetAttribute(attr);
                    if (string.IsNullOrWhiteSpace(s)) return null;
                    return double.TryParse(s, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var l) ? l : (double?)null;
                }
                rows.Add(new MeasurementRow
                {
                    TestName = name,
                    Value = num,
                    ValueText = num == null ? raw : null,
                    Lolim = ParseLim(_rules.AttrTestLolim),
                    Hilim = ParseLim(_rules.AttrTestHilim),
                    Unit = reader.GetAttribute(_rules.AttrTestUnit),
                    Rule = reader.GetAttribute(_rules.AttrTestRule),
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[分析采集] 测量值提取失败(不影响主解析): {ex.Message}");
            rows.Clear();
        }
        return rows;
    }

    /// <summary>PASS 文件轻量读取：FACTORY 取 USER/TESTER/FIXTURE_ID，继续读到首个 DUT 取 SN 即停（期2 补齐 tester/fixture，治具分母与 tester 对比依赖）。</summary>
    private (string? user, string? tester, string? fixtureId, string? sn) ReadFactoryOnly(string rawXml)
    {
        string? user = null, tester = null, fixtureId = null, sn = null;
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
                switch (reader.Name)
                {
                    case "FACTORY":
                        user = reader.GetAttribute(_rules.AttrFactoryUser);
                        tester = reader.GetAttribute(_rules.AttrTester);
                        fixtureId = reader.GetAttribute(_rules.AttrFixtureId);
                        break;
                    case "PANEL":
                        fixtureId ??= reader.GetAttribute(_rules.AttrFixtureId);
                        break;
                    case "DUT":
                        sn = reader.GetAttribute(_rules.AttrDutId);
                        return (user, tester, fixtureId, sn);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"ReadFactoryOnly 失败: {ex.Message}");
        }
        return (user, tester, fixtureId, sn);
    }
}
