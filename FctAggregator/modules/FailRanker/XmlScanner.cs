using System.Text.RegularExpressions;
using System.Xml;

namespace FctFailRanker;

public class FailItem
{
    public string Name = "";
    public string Value = "";
    public string Lolim = "";
    public string Hilim = "";
    public string Unit = "";

    public override string ToString() => Name;

    public string ToDetail()
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(Value)) parts.Add($"值={Value}");
        if (!string.IsNullOrEmpty(Lolim) || !string.IsNullOrEmpty(Hilim))
            parts.Add($"规格={Lolim}~{Hilim}");
        if (!string.IsNullOrEmpty(Unit)) parts.Add(Unit);
        return parts.Count > 0 ? $"{Name} ({string.Join(", ", parts)})" : Name;
    }
}

public class XmlRecord
{
    public string FilePath = "";
    public string Filename = "";
    public string Category = "";
    public string Model = "";
    public string TestDate = "";
    public string Sn = "";
    public string Station = "";
    public string User = "";
    public string Timestamp = "";
    public string Result = "";
    public bool IsDebug;
    public List<FailItem> FailItems = new();
}

public static class XmlScanner
{
    private static readonly string[] IgnoredFailSteps =
    {
        "Get Unit Information",
        "UUT Status Err",
    };

    private static bool IsIgnoredFailStep(string name)
    {
        foreach (var ig in IgnoredFailSteps)
            if (name.Contains(ig, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly Regex ModelRe = new(@"^E\d{7}$", RegexOptions.Compiled);
    private static bool IsValidModel(string name) => ModelRe.IsMatch(name);

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

            if (!IsValidModel(info.Model)) { skippedUnknown++; continue; }

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
                                rec.FailItems.Add(new FailItem
                                {
                                    Name = name,
                                    Value = reader.GetAttribute("VALUE") ?? "",
                                    Lolim = reader.GetAttribute("LOLIM") ?? "",
                                    Hilim = reader.GetAttribute("HILIM") ?? "",
                                    Unit = reader.GetAttribute("UNIT") ?? "",
                                });
                            }
                            break;
                    }
                }
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
