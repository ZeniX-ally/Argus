using System.Text.RegularExpressions;

namespace FctAggregator;

/// <summary>核心处理: 解析单个 xml -> TestRecord。按文件名前缀走 PASS 轻量 / FAIL 深度路径。</summary>
public class Processor
{
    private readonly AppConfig _cfg;
    private readonly string _defaultStation;

    // 路径: ...\{Online|Offline}\{型号}\{8位日期}\{文件名}
    private static readonly Regex PathRe = new(
        @"[\\/](?<category>Online|Offline)[\\/](?<model>[^\\/]+)[\\/](?<date>\d{8})[\\/](?<file>[^\\/]+)$",
        RegexOptions.Compiled);

    public Processor(AppConfig cfg, string defaultStation)
    {
        _cfg = cfg;
        _defaultStation = defaultStation;
    }

    public class PathInfo
    {
        public string Category = "", Model = "", TestDate = "", Filename = "";
        public string? Sn, ModelFromName;
    }

    public static PathInfo? ParsePath(string path)
    {
        var m = PathRe.Match(path);
        if (!m.Success) return null;
        var info = new PathInfo
        {
            Category = m.Groups["category"].Value,
            Model = m.Groups["model"].Value,
            TestDate = m.Groups["date"].Value,
            Filename = m.Groups["file"].Value,
        };
        // 文件名: 前缀_Fts_PEU_G49_机台_SN_时间_时间
        var stem = info.Filename;
        if (stem.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        stem = stem.Replace("(debug)", "").Replace("(Debug)", "");
        var parts = stem.Split('_');
        if (parts.Length >= 6)
        {
            info.Sn = parts[5];
            if (info.Sn.Length >= 8) info.ModelFromName = info.Sn[..8];
        }
        return info;
    }

    /// <summary>解析并分类。返回 null 表示跳过(debug/无法识别/解析失败)。</summary>
    public TestRecord? ParseAndClassify(string path)
    {
        path = Path.GetFullPath(path);
        if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return null;

        var info = ParsePath(path);
        if (info == null) return null;

        var filename = info.Filename;
        var prefix = filename.Length >= 2 ? filename[..2].ToUpperInvariant() : "";
        if (prefix is not ("P_" or "F_" or "O_"))
        {
            Logger.Warning($"[跳过] 未知文件名前缀: {filename}");
            return null;
        }

        var modelName = info.ModelFromName ?? info.Model;
        var snFromName = info.Sn;
        bool isPass = prefix == "P_";

        string result;
        string? user, tester = null, panelStatus = null, batchTs = null, failReason = null;
        bool hasFail = false;
        List<FailedTest> failedTests = new();

        if (isPass)
        {
            // PASS: 轻量读 USER 判 debug
            user = XmlParser.ReadUserOnly(path);
            if (Classifier.IsDebug(user)) return null;
            result = "PASS";
            panelStatus = "Passed";
            // PASS 不深度解析, 用目录日期(yyyyMMdd)转为日期戳供"今日"统计
            if (info.TestDate.Length == 8)
                batchTs = $"{info.TestDate[..4]}-{info.TestDate[4..6]}-{info.TestDate[6..8]}T00:00:00";
        }
        else
        {
            var pr = XmlParser.Parse(path);
            if (pr.Error) { AppState.IncParseError(); return null; }
            if (Classifier.IsDebug(pr.FactoryUser)) return null;
            result = Classifier.ClassifyByPrefix(filename, pr.HasFailItems);
            if (result == "INVALID") { AppState.IncParseError(); return null; }
            user = pr.FactoryUser;
            tester = pr.Tester;
            panelStatus = pr.PanelStatus;
            batchTs = pr.BatchTimestamp;
            failReason = pr.FailReason;
            hasFail = pr.HasFailItems;
            failedTests = pr.FailedTests;
            if (string.IsNullOrEmpty(snFromName)) snFromName = pr.Sn;
        }

        // batch_timestamp 保底: 若解析不到(或格式异常), 用目录日期, 保证当日统计能匹配
        // 统一规范为 yyyy-MM-dd 开头(前10位供当日统计用)
        if (string.IsNullOrEmpty(batchTs) || batchTs.Length < 10)
        {
            if (info.TestDate.Length == 8)
                batchTs = $"{info.TestDate[..4]}-{info.TestDate[4..6]}-{info.TestDate[6..8]}T00:00:00";
        }

        // 机台号: 优先 config/传入, 否则从 tester 提取, 再否则 UNKNOWN
        var stationId = _defaultStation;
        if (string.IsNullOrEmpty(stationId))
        {
            stationId = StationDetector.ExtractStationFromTester(tester) ?? "UNKNOWN";
        }

        long? size = null;
        try { size = new FileInfo(path).Length; } catch { }

        return new TestRecord
        {
            StationId = stationId,
            Model = modelName,
            Category = info.Category,
            TestDate = info.TestDate,
            Sn = snFromName,
            Result = result,
            XmlPath = path,
            FailReason = failReason,
            Tester = tester,
            PanelStatus = panelStatus,
            BatchTimestamp = batchTs,
            HasFailItems = hasFail,
            FailedTests = failedTests,
            FileSize = size,
        };
    }
}
