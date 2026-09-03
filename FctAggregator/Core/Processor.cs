namespace FctAggregator;

/// <summary>
/// 核心处理：解析单个 xml → TestRecord。
///
/// v3.5.0 解析智能化重构：原硬编码的"路径/前缀/SN/排除项/时间戳"逻辑下沉到
/// <see cref="Parsing.IResultParser"/> 责任链（内置 <see cref="Parsing.DefaultResultParser"/> 完整复刻旧逻辑）。
/// 本类只负责：读文件 → 委托解析注册表 → 把 <see cref="Parsing.ParseOutput"/> 映射成 TestRecord。
/// 默认构造使用全局 <see cref="Parsing.ParserRegistry.Instance"/>（启动时加载 parsers.json + 内置默认），
/// 行为与旧版完全兼容；也可注入自定义注册表（测试/多格式场景）。
/// </summary>
public class Processor
{
    private readonly AppConfig _cfg;
    private readonly string _defaultStation;
    private readonly Parsing.ParserRegistry _registry;
    private readonly Database? _db;

    /// <summary>默认构造：全局解析注册表 + 不记录解析失败日志。</summary>
    public Processor(AppConfig cfg, string defaultStation)
        : this(cfg, defaultStation, Parsing.ParserRegistry.Instance, null) { }

    /// <summary>可注入解析注册表（多格式/测试用）。默认使用全局单例（含内置默认解析器）。</summary>
    public Processor(AppConfig cfg, string defaultStation, Parsing.ParserRegistry registry, Database? db = null)
    {
        _cfg = cfg;
        _defaultStation = defaultStation;
        _registry = registry;
        _db = db;
    }

    /// <summary>解析并分类。返回 null 表示跳过(debug/无法识别/解析失败)。</summary>
    public TestRecord? ParseAndClassify(string path)
    {
        path = Path.GetFullPath(path);
        if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return null;

        string rawXml;
        try { rawXml = File.ReadAllText(path, System.Text.Encoding.UTF8); }
        catch (Exception ex)
        {
            Logger.Warning($"[解析] 读取文件失败，跳过 {path}: {ex.Message}");
            _db?.LogParseFailure(path, "read_error", "read file failed", _defaultStation);
            return null;
        }

        Parsing.ParseOutput? outp;
        try { outp = _registry.Resolve(path, rawXml); }
        catch (Exception ex)
        {
            Logger.Error($"[解析] 责任链异常 {path}: {ex.Message}");
            AppState.IncParseError();
            _db?.LogParseFailure(path, "registry_exception", ex.Message, _defaultStation);
            return null;
        }

        if (outp == null) { Logger.Warning($"[跳过] 无解析器适用: {path}"); return null; }
        if (outp.Skipped) return null;                       // debug/非目标格式
        if (outp.Error)
        {
            AppState.IncParseError();
            _db?.LogParseFailure(path, outp.ErrorCode, outp.SkipReason ?? "", outp.StationId);
            return null;
        }

        return new TestRecord
        {
            StationId = outp.StationId,
            Model = outp.Model,
            Category = outp.Category,
            TestDate = outp.TestDate,
            Sn = outp.Sn,
            Result = outp.Result,
            XmlPath = path,
            FailReason = outp.FailReason,
            Tester = outp.Tester,
            PanelStatus = outp.PanelStatus,
            FixtureId = outp.FixtureId,
            BatchTimestamp = outp.BatchTimestamp,
            HasFailItems = outp.HasFailItems,
            FailedTests = outp.FailedTests,
            FileSize = outp.FileSize,
        };
    }
}
