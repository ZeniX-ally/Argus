using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using FctAggregator;

namespace FctAggregator.Parsing;

/// <summary>
/// 一条可配置解析规则（对应 parsers.json 里的一个解析器实例）。
/// 把原 Processor/Classifier/XmlParser 的硬编码参数外置，实现"配置驱动"的通用解析。
/// </summary>
public sealed class ParserRuleSet
{
    /// <summary>内置默认规则集：完整复刻 v3.4.x 的 FCT BATCH 格式解析（路径/前缀/SN/排除项）。</summary>
    public static readonly ParserRuleSet Default = new()
    {
        Id = "default",
        Priority = 1000,
        PathPattern = @"[\\/](?<category>Online|Offline)[\\/](?<model>[^\\/]+)[\\/](?<date>\d{8})[\\/](?<file>[^\\/]+)$",
        SnSegment = 6,
        // 文件名前缀 → 结果（P_=PASS, F_=FAIL, O_=按 fail 项）。
        PrefixResults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["P_"] = "PASS", ["F_"] = "FAIL", ["O_"] = "AUTO",
        },
        // 解析 XML 时忽略的失败步骤（非产品真实不良）。
        IgnoredFailSteps = new List<string> { "Get Unit Information", "UUT Status Err" },
        // XML 属性名（与实际 BATCH 格式对齐；可配置以支持其他格式）。
        AttrBatchTs = "TIMESTAMP",
        AttrFactoryUser = "USER",
        AttrTester = "TESTER",
        AttrFixtureId = "FIXTURE_ID",   // v3.22.0 规格01 fixture 归因（BATCH XML 常见位置：FACTORY@FIXTURE_ID）
        AttrPanelStatus = "STATUS",
        AttrDutId = "ID",
        AttrTestName = "NAME",
        AttrTestStatus = "STATUS",
        AttrTestValue = "VALUE",
        AttrTestHilim = "HILIM",
        AttrTestLolim = "LOLIM",
        AttrTestUnit = "UNIT",
        AttrTestRule = "RULE",
        // 判 debug 的 USER 值（忽略大小写）。
        DebugUsers = new List<string> { "debug" },
        // 时间源优先级：文件名 → 目录日期（v3.4.x：产线 XML 的 BATCH/PANEL/DUT/GROUP 时间均不可信）。
        TimePriority = new List<string> { "filename", "directory" },
    };

    public string Id { get; set; } = "default";
    public int Priority { get; set; } = 1000;
    /// <summary>路径结构正则（命名捕获 category/model/date/file）。</summary>
    public string PathPattern { get; set; } = "";
    /// <summary>SN 取文件名的第几段（下划线分隔，从 1 计数；0 = 不取）。</summary>
    public int SnSegment { get; set; }
    /// <summary>文件名前缀 → 结果映射；值为 "AUTO" 表示按 fail 项判定（O_ 场景）。</summary>
    public Dictionary<string, string> PrefixResults { get; set; } = new();
    /// <summary>解析 XML 时忽略的失败步骤名（含即视为非真实不良）。</summary>
    public List<string> IgnoredFailSteps { get; set; } = new();
    public string AttrBatchTs { get; set; } = "TIMESTAMP";
    public string AttrFactoryUser { get; set; } = "USER";
    public string AttrTester { get; set; } = "TESTER";
    /// <summary>治具/机台位 ID 属性名（v3.22.0 规格01 fixture 归因用；XML 无该属性时保持 null = 前缀回退）。</summary>
    public string AttrFixtureId { get; set; } = "FIXTURE_ID";
    public string AttrPanelStatus { get; set; } = "STATUS";
public string AttrDutId { get; set; } = "ID";
    public string AttrTestName { get; set; } = "NAME";
    public string AttrTestStatus { get; set; } = "STATUS";
    public string AttrTestValue { get; set; } = "VALUE";
    public string AttrTestHilim { get; set; } = "HILIM";
    public string AttrTestLolim { get; set; } = "LOLIM";
    public string AttrTestUnit { get; set; } = "UNIT";
    public string AttrTestRule { get; set; } = "RULE";
    public List<string> DebugUsers { get; set; } = new();
    /// <summary>时间源优先级顺序（filename/directory）。</summary>
    public List<string> TimePriority { get; set; } = new();

    private Regex? _pathRe;
    /// <summary>编译后的路径正则（首次访问惰性编译；非法正则回落默认）。</summary>
    public Regex PathRegex
    {
        get
        {
            if (_pathRe != null) return _pathRe;
            try { _pathRe = new Regex(PathPattern, RegexOptions.Compiled); }
            catch (Exception ex)
            {
                Logger.Warning($"[解析配置] 路径正则非法，回落默认: {ex.Message}");
                _pathRe = new Regex(Default.PathPattern, RegexOptions.Compiled);
            }
            return _pathRe;
        }
    }

    public bool IsDebug(string? user)
    {
        if (string.IsNullOrEmpty(user)) return false;
        var u = user.Trim().ToLowerInvariant();
        return DebugUsers.Any(d => d.Equals(u, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// parsers.json 根结构：按顺序声明的解析器规则列表（责任链）。
/// 文件缺失/非法时回落内置 <see cref="ParserRuleSet.Default"/>。
/// </summary>
public sealed class ParserConfigDoc
{
    public List<ParserRuleSet> Parsers { get; set; } = new();
}

/// <summary>
/// 解析器注册表（责任链 + 插件）。
///
/// 两类来源：
/// 1. 配置驱动：parsers.json 里的 <see cref="ParserRuleSet"/> → 自动包装成 <see cref="ConfigurableResultParser"/>；
/// 2. 代码插件：实现 <see cref="IResultParser"/> 并调用 <see cref="Register"/> 挂入（如未来特殊格式）。
///
/// <see cref="Resolve"/> 按优先级排序的责任链依次尝试，第一个返回非 null 的胜出。
/// 内置 <see cref="DefaultResultParser"/> 始终兜底（即便 parsers.json 缺失也能解析现有格式）。
/// </summary>
public sealed class ParserRegistry
{
    private readonly List<IResultParser> _parsers = new();
    private static readonly object _lock = new();

    // v3.5.0 修复：Instance 必须始终可用。初始化即含内置默认解析器；
    // Load 重建后替换引用（此前 Load 的返回值被丢弃、Instance 恒空导致解析全失败）。
    private static ParserRegistry _instance = CreateDefault();

    /// <summary>全局注册表。默认含内置默认解析器（未调 Load 也可直接解析现有格式）；Load 后按配置重建。</summary>
    public static ParserRegistry Instance => _instance;

    private static ParserRegistry CreateDefault()
    {
        var reg = new ParserRegistry();
        reg._parsers.Add(new DefaultResultParser(ParserRuleSet.Default, null));
        return reg;
    }

    private ParserRegistry() { }

    /// <summary>用 parsers.json 内容（可空）初始化：加载配置规则 + 注册内置默认，并替换全局 Instance。线程安全。</summary>
    public static ParserRegistry Load(string? jsonText, string? defaultStation = null)
    {
        lock (_lock)
        {
            var reg = new ParserRegistry();
            try
            {
                if (!string.IsNullOrWhiteSpace(jsonText))
                {
                    var doc = JsonSerializer.Deserialize<ParserConfigDoc>(jsonText);
                    if (doc?.Parsers != null)
                    {
                        foreach (var rule in doc.Parsers)
                            reg._parsers.Add(new ConfigurableResultParser(rule));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[解析配置] parsers.json 解析失败，回落内置默认规则: {ex.Message}");
            }
            // 内置默认始终兜底（保证现有格式兼容，即使配置缺失）
            reg._parsers.Add(new DefaultResultParser(ParserRuleSet.Default, defaultStation));
            reg._parsers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            _instance = reg;
            return _instance;
        }
    }

    /// <summary>注册代码插件解析器（高优先级可插入链首）。</summary>
    public void Register(IResultParser parser)
    {
        lock (_lock)
        {
            _parsers.RemoveAll(p => p.Id == parser.Id);
            _parsers.Add(parser);
            _parsers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }
    }

    /// <summary>按责任链解析；全部返回 null 则整体返回 null（调用方跳过）。</summary>
    public ParseOutput? Resolve(string xmlPath, string rawXml)
    {
        List<IResultParser> snapshot;
        lock (_lock) snapshot = _parsers.ToList();
        foreach (var p in snapshot)
        {
            try
            {
                var r = p.Parse(xmlPath, rawXml);
                if (r != null) return r;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[解析] 解析器 {p.Id} 异常: {ex.Message}");
            }
        }
        return null;
    }
}
