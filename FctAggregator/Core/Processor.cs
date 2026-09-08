namespace FctAggregator;

public class Processor
{
    private readonly AppConfig _cfg;
    private readonly string _defaultStation;
    private readonly Parsing.ParserRegistry _registry;
    private readonly Database? _db;

    public Processor(AppConfig cfg, string defaultStation)
        : this(cfg, defaultStation, Parsing.ParserRegistry.Instance, null) { }

    public Processor(AppConfig cfg, string defaultStation, Parsing.ParserRegistry registry, Database? db = null)
    {
        _cfg = cfg;
        _defaultStation = defaultStation;
        _registry = registry;
        _db = db;
    }

    public TestRecord? ParseAndClassify(string path) => ParseAndClassify(path, out _);

    /// <summary>带失败分类的重载：暂时性失败（半成品 XML/文件被占用等）置 transientFailure=true 供引擎重试；永久性失败（路径不匹配/未知前缀/debug）保持 false。既有调用方走原签名不受影响。</summary>
    public TestRecord? ParseAndClassify(string path, out bool transientFailure)
    {
        transientFailure = false;
        path = Path.GetFullPath(path);
        if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return null;

        string rawXml;
        try { rawXml = File.ReadAllText(path, System.Text.Encoding.UTF8); }
        catch (Exception ex)
        {
            Logger.Warning($"[解析] 读取文件失败，跳过 {path}: {ex.Message}");
            _db?.LogParseFailure(path, "read_error", "read file failed", _defaultStation);
            transientFailure = true; // 读取失败多为写入器暂持句柄，属暂时性可重试
            return null;
        }

        Parsing.ParseOutput? outp;
        try { outp = _registry.Resolve(path, rawXml); }
        catch (Exception ex)
        {
            Logger.Error($"[解析] 责任链异常 {path}: {ex.Message}");
            AppState.IncParseError();
            _db?.LogParseFailure(path, "registry_exception", ex.Message, _defaultStation);
            transientFailure = true; // 责任链异常可能为瞬时问题，允许有限重试
            return null;
        }

        if (outp == null) { Logger.Warning($"[跳过] 无解析器适用: {path}"); return null; } // 永久性：不重试
        if (outp.Skipped) return null; // 永久性：路径不匹配/未知前缀/debug，不重试
        if (outp.Error)
        {
            AppState.IncParseError();
            _db?.LogParseFailure(path, outp.ErrorCode, outp.SkipReason ?? "", outp.StationId);
            transientFailure = true; // xml_malformed 等多为半成品文件，属暂时性可重试
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
            Measurements = outp.Measurements,
            FileSize = outp.FileSize,
        };
    }
}
