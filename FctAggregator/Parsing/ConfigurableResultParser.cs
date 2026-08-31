using System.Collections.Generic;
using FctAggregator;

namespace FctAggregator.Parsing;

/// <summary>
/// 配置驱动的解析器：把 parsers.json 里的一条 <see cref="ParserRuleSet"/> 包装成
/// <see cref="IResultParser"/> 责任链节点。其内部实现与 <see cref="DefaultResultParser"/> 同源
/// （均基于规则集解析），区别仅在于规则集来源（配置 vs 内置）。这样新增型号/格式时只需加配置，
/// 不动核心代码，实现"极高的通用与可拓展性"。
///
/// 注意：配置解析器与原内置默认在逻辑上等价，差异仅在字段名/路径/排除项等参数。
/// 若 parsers.json 未声明任何解析器，注册表仅挂内置默认，行为完全兼容现有 FCT 产线。
/// </summary>
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
