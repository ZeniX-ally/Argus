namespace FctAggregator;

public class Processor
{
    private readonly AppConfig _cfg;
    private readonly string _defaultStation;
    private readonly Parsing.ParserRegistry _registry;
    private readonly Database? _db;

    /// <summary>审计 A6：单文件读取硬上限 512MB，防失控 XML 整读内存 OOM（与 analyze_max_file_kb 测量值提取护栏相互独立）</summary>
    private const long OversizeHardCapBytes = 512L * 1024 * 1024;

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

        // 审计 A6：读前检查文件大小——数百 MB 失控 XML 曾先整读进内存再判断，可 OOM/卡死机台 PC。
        // 阈值取硬上限 512MB（远高于 analyze_max_file_kb 的测量值提取护栏 2048KB，
        // 不改变 2048KB~512MB 文件的入库行为，仅拦截内存失控场景），超限永久跳过并留痕
        try
        {
            if (new FileInfo(path).Length > OversizeHardCapBytes)
            {
                Logger.Warning($"[解析] 文件超硬上限({OversizeHardCapBytes / 1024 / 1024}MB)，跳过 {path}");
                _db?.LogParseFailure(path, "oversize", "file exceeds hard size cap", _defaultStation);
                return null; // 永久性：不重试
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[解析] 读取文件大小失败，跳过 {path}: {ex.Message}");
            _db?.LogParseFailure(path, "read_error", "stat file failed", _defaultStation);
            transientFailure = true;
            return null;
        }

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
        if (outp.Skipped)
        {
            // 审计 A4：Skipped 此前静默返回无痕消失——落 parse_failure_log 留痕（路径不匹配/未知前缀/debug 仍属永久性，不重试）
            // debug 跳过是常态（F_/O_ 调试 XML），WARNING 会在历史扫描时刷几千行；改 DEBUG，INFO 级现场 log 不再被淹。
            var reason = outp.SkipReason ?? "";
            if (reason.Equals("debug", StringComparison.OrdinalIgnoreCase))
                Logger.Debug($"[跳过] debug: {path}");
            else
                Logger.Warning($"[跳过] {reason}: {path}");
            _db?.LogParseFailure(path, "skip", reason, outp.StationId);
            return null; // 永久性：不重试
        }
        if (outp.Error)
        {
            AppState.IncParseError();
            _db?.LogParseFailure(path, outp.ErrorCode, outp.SkipReason ?? "", outp.StationId);
            transientFailure = true; // xml_malformed 等多为半成品文件，属暂时性可重试
            return null;
        }

        var rec = new TestRecord
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
            CycleChecked = true,
            CycleSeconds = XmlParser.ReadCycleSeconds(rawXml),
        };
        // 审计修复：原为 EffectiveMeasureRetentionDays——每个文件一次全表 MIN 扫描（首次扫描 O(N²)）；
        // 改走缓存入口（单调加宽，不会误删）。
        int keepDays = LearnPipeline.EffectiveMeasureRetentionDaysCached(_cfg, _db);
        if (rec.Measurements.Count > 0 && !Database.IsWithinRetention(rec.TestDate, keepDays))
            rec.Measurements = new List<MeasurementRow>();
        return rec;
    }
}
