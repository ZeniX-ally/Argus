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
        if (outp.Skipped) return null;
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
