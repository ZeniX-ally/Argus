using System.Collections.Generic;
using FctAggregator;

namespace FctAggregator.Parsing;

public sealed class ConfigurableResultParser : IResultParser
{
    private readonly DefaultResultParser _inner;

    public string Id => _rules.Id;
    public int Priority => _rules.Priority;

    private readonly ParserRuleSet _rules;

    /// <summary>审计修复：原构造只传 rules、丢掉 defaultStation——现场配了 parsers.json 时，
    /// 即使 config.station_id 配对了，命中自定义规则的文件仍回落 TESTER/UNKNOWN。</summary>
    public ConfigurableResultParser(ParserRuleSet rules, string? defaultStation = null)
    {
        _rules = rules;
        _inner = new DefaultResultParser(rules, defaultStation);
    }

    public ParseOutput? Parse(string xmlPath, string rawXml) => _inner.Parse(xmlPath, rawXml);
}
