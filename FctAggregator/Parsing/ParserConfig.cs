using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using FctAggregator;

namespace FctAggregator.Parsing;

public sealed class ParserRuleSet
{
    public static readonly ParserRuleSet Default = new()
    {
        Id = "default",
        Priority = 1000,
        PathPattern = @"[\\/](?<category>Online|Offline)[\\/](?<model>[^\\/]+)[\\/](?<date>\d{8})[\\/](?<file>[^\\/]+)$",
        SnSegment = 6,
        PrefixResults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["P_"] = "PASS", ["F_"] = "FAIL", ["O_"] = "AUTO",
        },
        IgnoredFailSteps = new List<string> { "Get Unit Information", "UUT Status Err" },
        AttrBatchTs = "TIMESTAMP",
        AttrFactoryUser = "USER",
        AttrTester = "TESTER",
        AttrFixtureId = "FIXTURE_ID",
        AttrPanelStatus = "STATUS",
        AttrDutId = "ID",
        AttrTestName = "NAME",
        AttrTestStatus = "STATUS",
        AttrTestValue = "VALUE",
        AttrTestHilim = "HILIM",
        AttrTestLolim = "LOLIM",
        AttrTestUnit = "UNIT",
        AttrTestRule = "RULE",
        DebugUsers = new List<string> { "debug" },
        TimePriority = new List<string> { "filename", "directory" },
    };

    public string Id { get; set; } = "default";
    public int Priority { get; set; } = 1000;
    public string PathPattern { get; set; } = "";
    public int SnSegment { get; set; }
    public Dictionary<string, string> PrefixResults { get; set; } = new();
    public List<string> IgnoredFailSteps { get; set; } = new();
    public string AttrBatchTs { get; set; } = "TIMESTAMP";
    public string AttrFactoryUser { get; set; } = "USER";
    public string AttrTester { get; set; } = "TESTER";
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
    public List<string> TimePriority { get; set; } = new();

    private Regex? _pathRe;
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

public sealed class ParserConfigDoc
{
    public List<ParserRuleSet> Parsers { get; set; } = new();
}

public sealed class ParserRegistry
{
    private readonly List<IResultParser> _parsers = new();
    private static readonly object _lock = new();

    private static ParserRegistry _instance = CreateDefault();

    public static ParserRegistry Instance => _instance;

    private static ParserRegistry CreateDefault()
    {
        var reg = new ParserRegistry();
        reg._parsers.Add(new DefaultResultParser(ParserRuleSet.Default, null));
        return reg;
    }

    private ParserRegistry() { }

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
            reg._parsers.Add(new DefaultResultParser(ParserRuleSet.Default, defaultStation));
            reg._parsers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            _instance = reg;
            return _instance;
        }
    }

    public void Register(IResultParser parser)
    {
        lock (_lock)
        {
            _parsers.RemoveAll(p => p.Id == parser.Id);
            _parsers.Add(parser);
            _parsers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }
    }

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
