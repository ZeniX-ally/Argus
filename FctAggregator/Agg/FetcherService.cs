using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FctShared;

namespace FctAggregator;

public sealed class FetchJob
{
    public string Id = Guid.NewGuid().ToString("N")[..8];
    public string Status = "pending";
    public string QueryJson = "";
    public int Total;
    public int Progress;
    public List<Dictionary<string, object?>> Preview = new();
    public string CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    public string FinishedAt = "";
    public string? Error;
    public string? FilePath;
    public string? FileName;
    public long FileSize;
    public string Format = "xlsx";
    public DateTime ExpireAt = DateTime.Now.AddHours(2);
}

public sealed class FetchFilter
{
    public string? Machine;
    public string? Model;
    public string? Station;
    public string? Category;
    public string? DateFrom;
    public string? DateTo;
    public int Limit = 5000;
    public int Offset;
    public bool PackZip;
    public string Format = "xlsx";
    public static FetchFilter FromJson(string json)
    {
        var f = new FetchFilter();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            f.Machine = GetStr(root, "machine");
            f.Model = GetStr(root, "model");
            f.Station = GetStr(root, "station") ?? GetStr(root, "station_id");
            f.Category = GetStr(root, "category");
            f.DateFrom = GetStr(root, "date_from") ?? GetStr(root, "dateFrom") ?? GetStr(root, "from");
            f.DateTo = GetStr(root, "date_to") ?? GetStr(root, "dateTo") ?? GetStr(root, "to");
            f.Format = (GetStr(root, "format") ?? "xlsx").Trim().ToLowerInvariant();
            if (f.Format != "xlsx" && f.Format != "csv" && f.Format != "zip") f.Format = "xlsx";
            f.PackZip = f.Format == "zip" || GetBool(root, "pack") || GetBool(root, "packZip");
            if (root.TryGetProperty("limit", out var pl) && pl.TryGetInt32(out var lim)) f.Limit = Math.Clamp(lim, 1, 20000);
            if (root.TryGetProperty("offset", out var po) && po.TryGetInt32(out var off)) f.Offset = Math.Max(0, off);
        }
        catch { }
        return f;
    }
    private static string? GetStr(JsonElement root, string name)
    {
        foreach (var p in root.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                return p.Value.GetString()?.Trim();
        return null;
    }
    private static bool GetBool(JsonElement root, string name)
    {
        foreach (var p in root.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind == JsonValueKind.True || (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()?.Trim().ToLowerInvariant() == "true");
        return false;
    }
    public static string NormalizeDate(string? d)
    {
        if (string.IsNullOrWhiteSpace(d)) return "";
        var s = d.Trim().Replace("-", "").Replace("/", "");
        if (s.Length >= 8) return s.Substring(0, 8);
        return s;
    }
}

public static class FetcherService
{
    private static readonly ConcurrentDictionary<string, FetchJob> _jobs = new();
    private static readonly object _lock = new();
    private const int MaxJobs = 100;

    public static string ExportRoot => Path.Combine(AppConfig.BaseDir, "data", "fetch_exports");

    public static FetchJob Create(string queryJson, Func<List<Dictionary<string, object?>>> producer)
    {
        var job = new FetchJob { QueryJson = queryJson, Status = "running", Progress = 5 };
        _jobs[job.Id] = job;
        _ = Task.Run(() =>
        {
            try
            {
                job.Progress = 10;
                var data = producer();
                job.Progress = 60;
                job.Preview = data.Take(100).ToList();
                job.Total = data.Count;
                job.Progress = 90;
                job.Status = "done";
                job.Progress = 100;
                job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch (Exception ex) { job.Status = "failed"; job.Error = ex.Message; job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); }
            TrimIfNeeded();
        });
        return job;
    }

    public static FetchJob CreateExport(AggDatabase db, string queryJson)
    {
        var filter = FetchFilter.FromJson(queryJson);
        var job = new FetchJob { QueryJson = queryJson, Status = "running", Progress = 5, Format = filter.Format };
        _jobs[job.Id] = job;
        Directory.CreateDirectory(ExportRoot);
        _ = Task.Run(() =>
        {
            try
            {
                job.Progress = 10;
                var allRows = QueryFiltered(db, filter);
                job.Total = allRows.Count;
                job.Preview = allRows.Take(100).Select(r => new Dictionary<string, object?>
                {
                    ["machine"] = r.Machine, ["sn"] = r.Sn, ["model"] = r.Model, ["result"] = r.Result,
                    ["test_date"] = r.TestDate, ["fail_reason"] = r.FailReason, ["tester"] = r.Tester,
                    ["station_id"] = r.StationId, ["category"] = r.Category, ["ts"] = r.Ts
                }).ToList();
                job.Progress = 30;

                int days = 30;
                if (!string.IsNullOrEmpty(filter.DateFrom) && !string.IsNullOrEmpty(filter.DateTo))
                {
                    try
                    {
                        var df = DateTime.ParseExact(FetchFilter.NormalizeDate(filter.DateFrom), "yyyyMMdd", null);
                        var dt = DateTime.ParseExact(FetchFilter.NormalizeDate(filter.DateTo), "yyyyMMdd", null);
                        days = Math.Clamp((int)(dt - df).TotalDays + 1, 1, 90);
                    }
                    catch { }
                }
                var trends = ReportService.GetTrend(db, filter.Machine, days);
                job.Progress = 45;
                var dist = ReportService.GetDistribution(db, "fail_reason", filter.Machine, 20);
                job.Progress = 55;
                var heat = ReportService.GetHeatmap(db, filter.Machine, days);
                job.Progress = 65;

                var xlsxPath = Path.Combine(ExportRoot, $"{job.Id}.xlsx");
                FetchXlsxWriter.WriteFetch(xlsxPath, allRows, trends, dist, heat, filter);
                job.Progress = 85;
                job.FilePath = xlsxPath;
                job.FileName = $"fetch_{DateTime.Now:yyyyMMdd_HHmmss}_{job.Id}.xlsx";

                if (filter.Format == "csv")
                {
                    var csvPath = Path.Combine(ExportRoot, $"{job.Id}.csv");
                    var headers = new[] { "时间", "机台", "型号", "SN", "测试日期", "失败原因", "测试员", "站点", "类别", "结果" };
                    var rows = allRows.Select(r => new[] { string.IsNullOrEmpty(r.Ts) ? r.IngestTs : r.Ts, r.Machine, r.Model, r.Sn, r.TestDate, r.FailReason, r.Tester, r.StationId, r.Category, r.Result });
                    CsvUtil.Write(csvPath, headers, rows);
                    job.FilePath = csvPath;
                    job.FileName = Path.GetFileName(csvPath).Replace(job.Id, $"fetch_{DateTime.Now:yyyyMMdd_HHmmss}_{job.Id}");
                }
                else if (filter.PackZip || filter.Format == "zip")
                {
                    var zipPath = Path.Combine(ExportRoot, $"{job.Id}.zip");
                    using (var zipFs = new FileStream(zipPath, FileMode.Create))
                    using (var zip = new ZipArchive(zipFs, ZipArchiveMode.Create))
                    {
                        zip.CreateEntryFromFile(xlsxPath, Path.GetFileName(xlsxPath));
                        var headers = new[] { "时间", "机台", "型号", "SN", "测试日期", "失败原因", "测试员", "站点", "类别", "结果" };
                        var rows = allRows.Select(r => new[] { string.IsNullOrEmpty(r.Ts) ? r.IngestTs : r.Ts, r.Machine, r.Model, r.Sn, r.TestDate, r.FailReason, r.Tester, r.StationId, r.Category, r.Result });
                        var csvBytes = CsvUtil.BuildSimpleBytes(headers, rows);
                        var csvEntry = zip.CreateEntry($"fetch_{DateTime.Now:yyyyMMdd}.csv");
                        using var es = csvEntry.Open();
                        es.Write(csvBytes, 0, csvBytes.Length);
                    }
                    job.FilePath = zipPath;
                    job.FileName = $"fetch_{DateTime.Now:yyyyMMdd_HHmmss}_{job.Id}.zip";
                    job.Format = "zip";
                }

                if (job.FilePath != null && File.Exists(job.FilePath))
                    job.FileSize = new FileInfo(job.FilePath).Length;
                job.Status = "done";
                job.Progress = 100;
                job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch (Exception ex)
            {
                job.Status = "failed";
                job.Error = ex.Message;
                job.FinishedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }
            TrimIfNeeded();
            PurgeExpired();
        });
        return job;
    }

    private static List<AggFailRow> QueryFiltered(AggDatabase db, FetchFilter f)
    {
        var limit = Math.Min(f.Limit + f.Offset + 100, 20000);
        var baseRows = db.QueryFails(limit, string.IsNullOrWhiteSpace(f.Machine) ? null : f.Machine.Trim(), 0, null);
        var df = FetchFilter.NormalizeDate(f.DateFrom);
        var dt = FetchFilter.NormalizeDate(f.DateTo);
        var filtered = baseRows.Where(r =>
        {
            if (!string.IsNullOrWhiteSpace(f.Model) && !string.Equals(r.Model ?? "", f.Model!.Trim(), StringComparison.OrdinalIgnoreCase) && (r.Model ?? "").IndexOf(f.Model!.Trim(), StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (!string.IsNullOrWhiteSpace(f.Station) && !string.Equals(r.StationId ?? "", f.Station!.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrWhiteSpace(f.Category) && !string.Equals(r.Category ?? "", f.Category!.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(df) && string.CompareOrdinal(r.TestDate ?? "", df) < 0) return false;
            if (!string.IsNullOrEmpty(dt) && string.CompareOrdinal(r.TestDate ?? "", dt) > 0) return false;
            return true;
        }).ToList();
        if (f.Offset > 0) filtered = filtered.Skip(f.Offset).ToList();
        if (filtered.Count > f.Limit) filtered = filtered.Take(f.Limit).ToList();
        return filtered;
    }

    private static void TrimIfNeeded()
    {
        if (_jobs.Count > MaxJobs)
        {
            lock (_lock)
            {
                if (_jobs.Count > MaxJobs)
                {
                    var toRemove = _jobs.OrderBy(kv => kv.Value.CreatedAt).Take(50).Select(kv => kv.Key).ToList();
                    foreach (var k in toRemove)
                    {
                        if (_jobs.TryRemove(k, out var j) && !string.IsNullOrEmpty(j.FilePath))
                            try { if (File.Exists(j.FilePath)) File.Delete(j.FilePath); } catch { }
                    }
                }
            }
        }
    }

    private static void PurgeExpired()
    {
        var now = DateTime.Now;
        var expired = _jobs.Where(kv => kv.Value.ExpireAt < now).Select(kv => kv.Key).ToList();
        foreach (var k in expired)
            if (_jobs.TryRemove(k, out var j) && !string.IsNullOrEmpty(j.FilePath))
                try { if (File.Exists(j.FilePath)) File.Delete(j.FilePath); } catch { }
    }

    public static FetchJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;
    public static List<FetchJob> List(int limit = 20) => _jobs.Values.OrderByDescending(j => j.CreatedAt).Take(Math.Min(limit, 50)).ToList();
}

public static class ReportService
{
    public static List<Dictionary<string, object?>> GetTrend(AggDatabase db, string? machine, int days)
    {
        days = Math.Clamp(days, 1, 90);
        var to = DateTime.Now.ToString("yyyyMMdd");
        var from = DateTime.Now.AddDays(-days + 1).ToString("yyyyMMdd");
        var rows = db.QueryDailyStats(machine, from, to, 5000);
        var map = new Dictionary<string, (int total, int pass, int fail)>();
        foreach (var r in rows)
        {
            var key = r.TestDate;
            var cur = map.GetValueOrDefault(key);
            cur.total += r.Total;
            cur.pass += r.Pass;
            cur.fail += r.Fail;
            map[key] = cur;
        }
        var result = new List<Dictionary<string, object?>>();
        var curDate = DateTime.Now.AddDays(-days + 1);
        for (int i = 0; i < days; i++)
        {
            var ymd = curDate.AddDays(i).ToString("yyyyMMdd");
            var (total, pass, fail) = map.GetValueOrDefault(ymd);
            var y = total > 0 ? Math.Round(pass * 100.0 / total, 2) : 100.0;
            result.Add(new Dictionary<string, object?>
            {
                ["date"] = ymd,
                ["total"] = total,
                ["pass"] = pass,
                ["fail"] = fail,
                ["yield"] = y,
            });
        }
        return result;
    }

    public static List<Dictionary<string, object?>> GetDistribution(AggDatabase db, string field, string? machine, int limit)
    {
        field = (field ?? "fail_reason").Trim().ToLowerInvariant();
        if (field != "model" && field != "fail_reason" && field != "station_id" && field != "category")
            field = "fail_reason";
        limit = Math.Clamp(limit, 1, 100);
        var rows = db.QueryFails(limit * 10, machine);
        var counter = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var key = field switch
            {
                "model" => r.Model ?? "",
                "station_id" => r.StationId ?? "",
                "category" => r.Category ?? "",
                _ => r.FailReason ?? "",
            };
            if (string.IsNullOrWhiteSpace(key)) key = "(空)";
            counter[key] = counter.GetValueOrDefault(key) + 1;
        }
        return counter.OrderByDescending(kv => kv.Value).Take(limit).Select(kv => new Dictionary<string, object?>
        {
            ["label"] = kv.Key,
            ["count"] = kv.Value,
        }).ToList();
    }

    public static Dictionary<string, object?> GetHeatmap(AggDatabase db, string? machine, int days)
    {
        days = Math.Clamp(days, 1, 90);
        var to = DateTime.Now.ToString("yyyyMMdd");
        var from = DateTime.Now.AddDays(-days + 1).ToString("yyyyMMdd");
        var rows = db.QueryFails(5000, machine);
        var filtered = string.IsNullOrEmpty(from) ? rows : rows.Where(r => string.CompareOrdinal(r.TestDate ?? "", from) >= 0 && string.CompareOrdinal(r.TestDate ?? "", to) <= 0).ToList();
        var machines = filtered.Select(r => r.Machine).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (string.IsNullOrWhiteSpace(machine) && machines.Count == 0)
        {
            var stats = db.QueryDailyStats(null, from, to, 5000);
            machines = stats.Select(s => s.Machine).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        }
        else if (!string.IsNullOrWhiteSpace(machine))
        {
            machines = new List<string> { machine!.Trim() };
        }
        var dates = new List<string>();
        var cur = DateTime.Now.AddDays(-days + 1);
        for (int i = 0; i < days; i++) dates.Add(cur.AddDays(i).ToString("yyyyMMdd"));
        var mat = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in machines) mat[m] = dates.ToDictionary(d => d, _ => 0);
        foreach (var r in filtered)
        {
            var d = r.TestDate ?? "";
            if (!dates.Contains(d)) continue;
            if (!mat.TryGetValue(r.Machine, out var row)) continue;
            row[d] = row.GetValueOrDefault(d) + 1;
        }
        return new Dictionary<string, object?>
        {
            ["machines"] = machines,
            ["dates"] = dates,
            ["matrix"] = mat,
            ["days"] = days,
        };
    }
}

internal static class FetchXlsxWriter
{
    private const int S_NORMAL = 0;
    private const int S_HEADER = 1;
    private const int S_TEXT_C = 2;
    private const int S_NUM_C = 3;

    public static void WriteFetch(string path, List<AggFailRow> rows, List<Dictionary<string, object?>> trends, List<Dictionary<string, object?>> dists, Dictionary<string, object?> heat, FetchFilter filter)
    {
        var sheets = new List<Xlsx.Sheet>
        {
            BuildDetail(rows, filter),
            BuildTrend(trends),
            BuildDist(dists),
            BuildHeat(heat),
        };
        Xlsx.Write(path, sheets, Styles());
    }

    private static Xlsx.Sheet BuildDetail(List<AggFailRow> rows, FetchFilter filter)
    {
        var sh = new Xlsx.Sheet { Name = "捞取清单", FreezeRows = 1 };
        sh.ColWidths.AddRange(new double[] { 20, 12, 12, 34, 12, 30, 12, 12, 12, 10 });
        string[] hd = { "时间", "机台", "型号", "SN", "测试日期", "失败原因", "测试员", "站点", "类别", "结果" };
        sh.Rows.Add(hd.Select(h => Xlsx.T(h, S_HEADER)).ToList());
        foreach (var r in rows)
        {
            sh.Rows.Add(new List<Xlsx.Cell>
            {
                Xlsx.T(string.IsNullOrEmpty(r.Ts) ? r.IngestTs : r.Ts, S_TEXT_C),
                Xlsx.T(r.Machine, S_TEXT_C),
                Xlsx.T(r.Model, S_TEXT_C),
                Xlsx.T(r.Sn, S_NORMAL),
                Xlsx.T(r.TestDate, S_TEXT_C),
                Xlsx.T(r.FailReason, S_NORMAL),
                Xlsx.T(r.Tester, S_TEXT_C),
                Xlsx.T(r.StationId, S_TEXT_C),
                Xlsx.T(r.Category, S_TEXT_C),
                Xlsx.T(r.Result, S_TEXT_C),
            });
        }
        if (rows.Count == 0)
            sh.Rows.Add(new List<Xlsx.Cell> { Xlsx.T("（无命中，按当前筛选条件未找到记录）", S_NORMAL) });
        return sh;
    }

    private static Xlsx.Sheet BuildTrend(List<Dictionary<string, object?>> trends)
    {
        var sh = new Xlsx.Sheet { Name = "趋势", FreezeRows = 1 };
        sh.ColWidths.AddRange(new double[] { 14, 10, 10, 10, 12, 10 });
        sh.Rows.Add(new[] { "日期", "总数", "PASS", "FAIL", "良率(%)", "备注" }.Select(h => Xlsx.T(h, S_HEADER)).ToList());
        foreach (var t in trends)
        {
            var date = t["date"]?.ToString() ?? "";
            var total = Convert.ToInt32(t["total"] ?? 0);
            var pass = Convert.ToInt32(t["pass"] ?? 0);
            var fail = Convert.ToInt32(t["fail"] ?? 0);
            var y = Convert.ToDouble(t["yield"] ?? 100.0);
            sh.Rows.Add(new List<Xlsx.Cell>
            {
                Xlsx.T(date, S_TEXT_C),
                Xlsx.N(total, S_NUM_C),
                Xlsx.N(pass, S_NUM_C),
                Xlsx.N(fail, S_NUM_C),
                Xlsx.N(Math.Round(y,2), S_NUM_C),
                Xlsx.T(y < 90 ? "低良率" : "", S_NORMAL),
            });
        }
        return sh;
    }

    private static Xlsx.Sheet BuildDist(List<Dictionary<string, object?>> dists)
    {
        var sh = new Xlsx.Sheet { Name = "分布", FreezeRows = 1 };
        sh.ColWidths.AddRange(new double[] { 40, 12, 16 });
        sh.Rows.Add(new[] { "维度值", "次数", "占比(%)" }.Select(h => Xlsx.T(h, S_HEADER)).ToList());
        int total = dists.Sum(d => Convert.ToInt32(d["count"] ?? 0));
        if (total == 0) total = 1;
        foreach (var d in dists)
        {
            var label = d["label"]?.ToString() ?? "";
            var cnt = Convert.ToInt32(d["count"] ?? 0);
            var pct = Math.Round(cnt * 100.0 / total, 2);
            sh.Rows.Add(new List<Xlsx.Cell> { Xlsx.T(label, S_NORMAL), Xlsx.N(cnt, S_NUM_C), Xlsx.N(pct, S_NUM_C) });
        }
        if (dists.Count == 0) sh.Rows.Add(new List<Xlsx.Cell> { Xlsx.T("(无数据)", S_NORMAL), Xlsx.N(0), Xlsx.N(0) });
        return sh;
    }

    private static Xlsx.Sheet BuildHeat(Dictionary<string, object?> heat)
    {
        var sh = new Xlsx.Sheet { Name = "热力", FreezeRows = 1 };
        var machines = heat.TryGetValue("machines", out var mv) ? (mv as List<string> ?? new List<string>()) : new List<string>();
        var dates = heat.TryGetValue("dates", out var dv) ? (dv as List<string> ?? new List<string>()) : new List<string>();
        var matrix = heat.TryGetValue("matrix", out var mm) ? (mm as Dictionary<string, Dictionary<string, int>> ?? new Dictionary<string, Dictionary<string, int>>()) : new Dictionary<string, Dictionary<string, int>>();
        var header = new List<Xlsx.Cell> { Xlsx.T("机台\\日期", S_HEADER) };
        foreach (var d in dates) header.Add(Xlsx.T(d, S_HEADER));
        sh.ColWidths.Add(14);
        foreach (var _ in dates) sh.ColWidths.Add(10);
        sh.Rows.Add(header);
        foreach (var m in machines)
        {
            var row = new List<Xlsx.Cell> { Xlsx.T(m, S_NORMAL) };
            var rowMap = matrix.TryGetValue(m, out var rm) ? rm : new Dictionary<string, int>();
            foreach (var d in dates)
            {
                var cnt = rowMap.GetValueOrDefault(d, 0);
                row.Add(Xlsx.N(cnt, S_NUM_C));
            }
            sh.Rows.Add(row);
        }
        if (machines.Count == 0) sh.Rows.Add(new List<Xlsx.Cell> { Xlsx.T("(无机台)", S_NORMAL) });
        return sh;
    }

    private static string Styles()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sb.Append("<fonts count=\"2\">");
        sb.Append("<font><sz val=\"11\"/><name val=\"微软雅黑\"/></font>");
        sb.Append("<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"微软雅黑\"/></font>");
        sb.Append("</fonts>");
        sb.Append("<fills count=\"3\">");
        sb.Append("<fill><patternFill patternType=\"none\"/></fill>");
        sb.Append("<fill><patternFill patternType=\"gray125\"/></fill>");
        sb.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF4472C4\"/></patternFill></fill>");
        sb.Append("</fills>");
        sb.Append("<borders count=\"2\">");
        sb.Append("<border><left/><right/><top/><bottom/><diagonal/></border>");
        sb.Append("<border><left style=\"thin\"><color rgb=\"FFBFBFBF\"/></left><right style=\"thin\"><color rgb=\"FFBFBFBF\"/></right><top style=\"thin\"><color rgb=\"FFBFBFBF\"/></top><bottom style=\"thin\"><color rgb=\"FFBFBFBF\"/></bottom><diagonal/></border>");
        sb.Append("</borders>");
        sb.Append("<cellStyleXfs count=\"1\"><xf/></cellStyleXfs>");
        sb.Append("<cellXfs count=\"4\">");
        sb.Append("<xf fontId=\"0\" fillId=\"0\" borderId=\"1\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" wrapText=\"0\"/></xf>");
        sb.Append("<xf fontId=\"1\" fillId=\"2\" borderId=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"0\"/></xf>");
        sb.Append("<xf fontId=\"0\" fillId=\"0\" borderId=\"1\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"0\"/></xf>");
        sb.Append("<xf fontId=\"0\" fillId=\"0\" borderId=\"1\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"0\"/></xf>");
        sb.Append("</cellXfs>");
        sb.Append("<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");
        sb.Append("</styleSheet>");
        return sb.ToString();
    }
}
