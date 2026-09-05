using System.Collections.Generic;
using FctAggregator;

namespace FctAggregator.Parsing;

public sealed class ConfigurableResultParser : IResultParser
{
    private readonly DefaultResultParser _inner;

    public string Id => _rules.Id;
    public int Priority => _rules.Priority;

    private readonly ParserRuleSet _rules;

    public ConfigurableResultParser(ParserRuleSet rules)
    {
        _rules = rules;
        _inner = new DefaultResultParser(rules);
    }

    public ParseOutput? Parse(string xmlPath, string rawXml) => _inner.Parse(xmlPath, rawXml);
}
