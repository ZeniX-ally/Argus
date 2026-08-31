using System.Collections.Generic;
using System.Text.RegularExpressions;
using FctAggregator;

namespace FctAggregator.Parsing;

/// <summary>
/// 结果解析插件接口（v3.5.0 解析智能化重构的核心抽象）。
///
/// 设计目标：把原 <see cref="Processor"/> 的硬编码解析（路径结构/文件名前缀/SN 段位/排除项/
/// 时间戳提取）与 <see cref="Classifier"/> 的前缀分类，抽象成"可配置、可扩展"的解析器。
/// 新增型号/测试格式时，只需在 parsers.json 注册一条规则，或实现本接口挂到 <see cref="ParserRegistry"/>，
/// 核心采集链路（Engine→Processor）无需改动 —— 实现极高的通用与可拓展性。
///
/// 契约：
/// - 输入：XML 完整路径 + 解析出的原始 XML 文本（已读取，避免解析器各自重复 IO）。
/// - 输出：<see cref="ParseOutput"/>；返回 null 表示该解析器"不认识"此文件（交给下一个解析器/跳过）。
/// - 解析器之间组成责任链（按优先级排序），第一个返回非 null 的胜出。
/// </summary>
public interface IResultParser
{
    /// <summary>解析器唯一标识（parsers.json 的 key，也用于日志/调试）。</summary>
    string Id { get; }

    /// <summary>优先级：数字越小越先尝试（内置默认 = 1000）。</summary>
    int Priority { get; }

    /// <summary>
    /// 解析单个 XML。返回 null 表示该解析器不适用（文件跳过/非目标格式）；
    /// 否则给出结构化结果（含分类、失败项、时间源等）。解析失败应返回带 Error=true 的输出，
    /// 由调用方决定是记解析错误还是跳过（与原 Processor 行为一致）。
    /// </summary>
    ParseOutput? Parse(string xmlPath, string rawXml);
}

/// <summary>
/// 解析输出（独立于具体解析实现，供 Processor/Database/推送链共用）。
/// 字段与原 <see cref="TestRecord"/> 候选字段对齐，减少转换成本。
/// </summary>
public class ParseOutput
{
    public bool Skipped;            // 主动跳过（debug/非目标）
    public bool Error;              // 解析异常（调用方记解析错误）
    public string? SkipReason;

    public string Result = "";      // PASS/FAIL/INTERRUPTED/INVALID
    public string StationId = "";
    public string Model = "";
    public string Category = "";    // Online/Offline
    public string TestDate = "";    // yyyyMMdd
    public string? Sn;
    public string? FailReason;
    public string? Tester;
    public string? PanelStatus;
    public string? BatchTimestamp;  // 最终采用的"真实测试时间"源（PANEL→BATCH→文件名→目录）
    public bool HasFailItems;
    public List<FailedTest> FailedTests { get; set; } = new();
    public long? FileSize;
}

/// <summary>
/// 路径/文件名元信息提取器：把"路径结构 + 文件名段位"抽象成可配置规则
/// （原 Processor.ParsePath 的硬编码正则/段位逻辑外置）。
/// 解析器可用它快速拿到 category/model/date/prefix/sn 等雏形，再决定如何分类。
/// </summary>
public sealed class PathMeta
{
    public string Category = "";
    public string Model = "";
    public string TestDate = "";
    public string Filename = "";
    public string? Sn;
    public string? ModelFromName;
    public string Prefix = "";      // 文件名前 2 字符（P_/F_/O_）
    public string? FileTime;         // 文件名里独立时间戳段（14/17 位）

    private static readonly Regex TsSegRe =
        new(@"(?:^|_)(\d{14}|\d{17})(?=_|$)", RegexOptions.Compiled);

    /// <summary>按给定规则从路径解析元信息；规则为 null 时回落内置默认行为。</summary>
    public static PathMeta? FromPath(string path, ParserRuleSet? rules)
    {
        rules ??= ParserRuleSet.Default;
        var m = rules.PathRegex.Match(path);
        if (!m.Success) return null;
        string cat = m.Groups["category"].Value;
        string model = m.Groups["model"].Value;
        string date = m.Groups["date"].Value;
        string file = m.Groups["file"].Value;

        var stem = file;
        if (stem.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        stem = stem.Replace("(debug)", "").Replace("(Debug)", "");
        var parts = stem.Split('_');

        var info = new PathMeta
        {
            Category = cat,
            Model = model,
            TestDate = date,
            Filename = file,
            Prefix = file.Length >= 2 ? file[..2].ToUpperInvariant() : "",
        };
        if (parts.Length >= rules.SnSegment && rules.SnSegment > 0)
        {
            var sn = parts[rules.SnSegment - 1];
            info.Sn = sn;
            if (sn.Length >= 8) info.ModelFromName = sn[..8];
        }
        // 注意：这里只做「原始提取」。FileTime 可能是 SN/批次号被误当时间，
        // 是否可信由 DefaultResultParser 用 TimeUtil.ResolveFileNameTime 的系统时间
        // 近邻校验决定（域1 智能时间）：偏差过大则判为误匹配，回退目录日期。
        var ft = TsSegRe.Match(stem);
        if (ft.Success) info.FileTime = ft.Groups[1].Value;
        return info;
    }
}
