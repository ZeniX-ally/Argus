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
        /// <summary>文件名里的时间段(第 7 段, 17 位 yyyyMMddHHmmssfff)。BATCH 时间缺失时的兜底。</summary>
        public string? FileTime;
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
        // 文件名时间戳（v3.4.13 加固）：不再依赖固定段位 —— SN 若含下划线会把段位挤错，
        // 改为从文件名里找**独立段**（下划线边界）的第一个 14/17 位纯数字段（yyyyMMddHHmmss[fff]）。
        // 真实样本：..._20260814072417293_2026813232417386 → 第 7 段 17 位命中；第 8 段 16 位非标准自动跳过。
        var ftMatch = TimeStampSegRe.Match(stem);
        if (ftMatch.Success) info.FileTime = ftMatch.Groups[1].Value;
        return info;
    }

    /// <summary>文件名里的独立时间戳段：`_yyyyMMddHHmmss_` 或 `_yyyyMMddHHmmssfff_`（14/17 位纯数字）。</summary>
    private static readonly Regex TimeStampSegRe = new(
        @"(?:^|_)(\d{14}|\d{17})(?=_|$)",
        RegexOptions.Compiled);

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
        string? user, tester = null, panelStatus = null, batchTs = null, panelTs = null, failReason = null;
        bool hasFail = false;
        List<FailedTest> failedTests = new();

        if (isPass)
        {
            // PASS: 轻量读 USER 判 debug
            user = XmlParser.ReadUserOnly(path);
            if (Classifier.IsDebug(user)) return null;
            result = "PASS";
            panelStatus = "Passed";
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
            panelTs = pr.PanelTimestamp;
            failReason = pr.FailReason;
            hasFail = pr.HasFailItems;
            failedTests = pr.FailedTests;
            if (string.IsNullOrEmpty(snFromName)) snFromName = pr.Sn;
        }

        // batch_timestamp 时间源优先级（v3.4.12 修复）: PANEL(真实测试时间) → BATCH(session 开始) → 文件名 → 目录日期。
        // ⚠ 真实产线样本实测踩坑：BATCH@TIMESTAMP 是测试 session 的打开时间，可能比实际测试早数天
        //   （样本 BATCH=2026-07-30 18:21 而 PANEL=2026-08-14 07:17），导致推送卡片/待办显示的时间
        //   与文件名时间(08-14 07:24)明显不一致。PANEL 才是这条记录的真实测试时间。
        // ⚠ 旧实现用 Length<10 判有效性: "2026-07-22T12:47:00.213+08:00" 长度够但格式怪时也过了,
        //   保底只在"真正解析不出时间"时触发, 保证显示/推送/待办的时间永远能读。
        var ts = panelTs;
        if (string.IsNullOrEmpty(ts) || TimeUtil.Normalize(ts).Length == 0)
            ts = batchTs;
        if (string.IsNullOrEmpty(ts) || TimeUtil.Normalize(ts).Length == 0)
        {
            if (info.FileTime != null)
                ts = TimeUtil.Normalize(info.FileTime);
            else if (info.TestDate.Length == 8)
                ts = $"{info.TestDate[..4]}-{info.TestDate[4..6]}-{info.TestDate[6..8]}T00:00:00";
        }
        batchTs = ts;

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
