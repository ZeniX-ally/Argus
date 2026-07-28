using System.Text.RegularExpressions;
using System.Xml;

namespace FctFailRanker;

/// <summary>
/// 单个 XML 的解析结果。业务规则与主项目 FCT-Aggregator 保持一致:
/// 分类看文件名前缀 P_/F_/O_; USER==debug 跳过; 失败项排除 "Get Unit Information" 和 "UUT Status Err"。
/// </summary>
public class XmlRecord
{
    public string FilePath = "";
    public string Filename = "";
    public string Category = "";     // Online / Offline
    public string Model = "";        // 型号(SN 前 8 位)
    public string TestDate = "";     // 目录日期 yyyyMMdd
    public string Sn = "";
    public string Station = "";      // 机台 (TESTER)
    public string User = "";         // FACTORY.USER
    public string Timestamp = "";    // BATCH.TIMESTAMP
    public string Result = "";       // PASS / FAIL / INTERRUPTED
    public bool IsDebug;
    public List<string> FailItems = new();   // 失败项名称(已排除 Get Unit Information / UUT Status Err)
}

public static class XmlScanner
{
    // 不计入不良的失败项(包含匹配, 忽略大小写)。
    // Get Unit Information: 获取单元信息失败, 不上传 MES;
    // UUT Status Err: 被测单元状态错误(非产品真实不良)。
    private static readonly string[] IgnoredFailSteps =
    {
        "Get Unit Information",
        "UUT Status Err",
    };

    /// <summary>判断该失败项是否属于应排除项(包含匹配, 忽略大小写)。</summary>
    private static bool IsIgnoredFailStep(string name)
    {
        foreach (var ig in IgnoredFailSteps)
            if (name.Contains(ig, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // 型号目录校验: 与主项目 StationDetector.IsValidModel 一致 (E + 7位数字, 如 E3002781)
    private static readonly Regex ModelRe = new(@"^E\d{7}$", RegexOptions.Compiled);
    private static bool IsValidModel(string name) => ModelRe.IsMatch(name);

    /// <summary>
    /// 扫描目录, 返回落在 [start, end] (按目录日期) 内的所有 XML 记录。
    /// end 为当天 23:59, 即含 end 当天。
    /// </summary>
    public static List<XmlRecord> Scan(
        string resultsRoot, DateTime start, DateTime end,
        Action<string>? log = null, Action<int, int>? progress = null)
    {
        var records = new List<XmlRecord>();
        if (!Directory.Exists(resultsRoot))
        {
            log?.Invoke($"[错误] 目录不存在: {resultsRoot}");
            return records;
        }

        var files = Directory.EnumerateFiles(resultsRoot, "*.xml", SearchOption.AllDirectories).ToList();
        log?.Invoke($"共发现 {files.Count} 个 XML 文件, 开始按日期过滤...");

        int done = 0, matched = 0, skippedDebug = 0, skippedRange = 0, skippedUnknown = 0;
        foreach (var file in files)
        {
            done++;
            if (done % 200 == 0) progress?.Invoke(done, files.Count);

            var info = ParsePath(file);
            if (info == null) { skippedUnknown++; continue; }

            // 型号目录校验: 与主项目 DiscoverModels 一致, 非 E+7位数字的目录不扫
            if (!IsValidModel(info.Model)) { skippedUnknown++; continue; }

            // 目录日期过滤
            if (!TryParseDate(info.TestDate, out var d)) { skippedUnknown++; continue; }
            if (d < start.Date || d > end.Date) { skippedRange++; continue; }

            var rec = ParseFile(file, info);
            if (rec == null) { skippedUnknown++; continue; }
            if (rec.IsDebug) { skippedDebug++; continue; }

            records.Add(rec);
            matched++;
        }
        progress?.Invoke(files.Count, files.Count);
        log?.Invoke($"过滤完成: 命中 {matched}, 超范围 {skippedRange}, debug {skippedDebug}, 跳过 {skippedUnknown}");
        return records;
    }

    private class PathInfo
    {
        public string Category = "", Model = "", TestDate = "", Filename = "";
        public string? Sn, ModelFromName, Prefix;
    }

    private static PathInfo? ParsePath(string path)
    {
        // ...\{Online|Offline}\{型号}\{yyyyMMdd}\{文件名}
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Length < 4) return null;

        var filename = parts[^1];
        var date = parts[^2];
        var model = parts[^3];
        var category = parts[^4];
        if (category != "Online" && category != "Offline") return null;

        var info = new PathInfo
        {
            Category = category,
            Model = model,
            TestDate = date,
            Filename = filename,
        };

        // 文件名: 前缀_Fts_PEU_G49_机台_SN_时间_时间
        var stem = filename;
        if (stem.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
        stem = stem.Replace("(debug)", "").Replace("(Debug)", "");
        var seg = stem.Split('_');
        info.Prefix = filename.Length >= 2 ? filename[..2].ToUpperInvariant() : "";
        if (seg.Length >= 6)
        {
            info.Sn = seg[5];
            if (info.Sn.Length >= 8) info.ModelFromName = info.Sn[..8];
        }
        return info;
    }

    private static bool TryParseDate(string yyyymmdd, out DateTime d)
    {
        d = default;
        return yyyymmdd.Length == 8
            && DateTime.TryParseExact(yyyymmdd, "yyyyMMdd", null,
                System.Globalization.DateTimeStyles.None, out d);
    }

    private static XmlRecord? ParseFile(string path, PathInfo info)
    {
        var prefix = info.Prefix ?? "";
        if (prefix is not ("P_" or "F_" or "O_")) return null;

        var rec = new XmlRecord
        {
            FilePath = path,
            Filename = info.Filename,
            Category = info.Category,
            Model = info.ModelFromName ?? info.Model,
            TestDate = info.TestDate,
            Sn = info.Sn ?? "",
        };

        bool isPass = prefix == "P_";
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
            };
            using var reader = XmlReader.Create(path, settings);

            if (isPass)
            {
                // PASS: 轻量只读 USER 判 debug
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.Name == "FACTORY")
                    {
                        rec.User = reader.GetAttribute("USER") ?? "";
                        rec.Station = reader.GetAttribute("TESTER") ?? "";
                    }
                    else if (reader.Name == "BATCH")
                        rec.Timestamp = reader.GetAttribute("TIMESTAMP") ?? "";
                    else if (reader.Name == "PANEL") break;
                }
                rec.Result = "PASS";
            }
            else
            {
                // F_ / O_: 深度解析, 收集失败项(排除忽略项)
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    switch (reader.Name)
                    {
                        case "BATCH":
                            rec.Timestamp = reader.GetAttribute("TIMESTAMP") ?? "";
                            break;
                        case "FACTORY":
                            rec.User = reader.GetAttribute("USER") ?? "";
                            rec.Station = reader.GetAttribute("TESTER") ?? "";
                            break;
                        case "DUT":
                            if (string.IsNullOrEmpty(rec.Sn))
                                rec.Sn = reader.GetAttribute("ID") ?? "";
                            break;
                        case "TEST":
                            if (reader.GetAttribute("STATUS") == "Failed")
                            {
                                var name = reader.GetAttribute("NAME") ?? "";
                                if (IsIgnoredFailStep(name)) break;
                                rec.FailItems.Add(name);
                            }
                            break;
                    }
                }
                // O_ 有真 fail -> FAIL, 否则 INTERRUPTED; F_ 恒 FAIL
                if (prefix == "F_") rec.Result = "FAIL";
                else rec.Result = rec.FailItems.Count > 0 ? "FAIL" : "INTERRUPTED";
            }
        }
        catch
        {
            return null;
        }

        rec.IsDebug = rec.User.Trim().Equals("debug", StringComparison.OrdinalIgnoreCase);
        return rec;
    }
}
