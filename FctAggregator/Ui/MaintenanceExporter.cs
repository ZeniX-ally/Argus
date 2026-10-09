using System.Text;

namespace FctAggregator;

public static class MaintenanceExporter
{
    private static readonly string[] Headers =
    {
        "ID", "故障项目", "设备型号", "设备SN", "故障描述",
        "严重度", "状态", "维修人", "维修措施", "备注", "创建时间", "更新时间",
    };

    private static string[] RowOf(MaintenanceRecord m) => new[]
    {
        m.Id.ToString(),
        m.FailItem,
        m.EquipmentModel,
        m.EquipmentSn,
        m.FailReason,
        MaintenanceMeta.SeverityZhOf(m.Severity),
        MaintenanceMeta.ZhOf(m.Status),
        m.Resolver,
        m.Resolution,
        m.Notes,
        m.CreatedAt,
        m.UpdatedAt,
    };

    public static void ExportCsv(string path, IEnumerable<MaintenanceRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("维修记录导出");
        sb.AppendLine($"导出时间,{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine(string.Join(",", Headers.Select(CsvUtil.Esc)));
        foreach (var m in records)
            sb.AppendLine(string.Join(",", RowOf(m).Select(CsvUtil.Esc)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>与本机库同目录的活表：一条维修记录一行，状态变更改原行。</summary>
    public const string LogFileName = "维修日志.csv";

    public static string LogPathFor(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (string.IsNullOrEmpty(dir)) dir = ".";
        return Path.Combine(dir, LogFileName);
    }

    private static readonly object LogGate = new();

    /// <summary>按 ID 写入或覆盖原行。字段里的换行压成空格，保证一条记录占物理上的一行。</summary>
    public static void UpsertLogRow(string dbPath, MaintenanceRecord m)
    {
        var line = string.Join(",", RowOf(m).Select(FlatCell));
        lock (LogGate)
        {
            var path = LogPathFor(dbPath);
            var lines = ReadLog(path);
            int found = -1;
            var id = m.Id.ToString();
            for (int i = 1; i < lines.Count; i++)
            {
                if (FirstField(lines[i]) == id) { found = i; break; }
            }
            if (found >= 0) lines[found] = line;
            else lines.Add(line);
            WriteLog(path, lines);
        }
    }

    public static void RemoveLogRow(string dbPath, int id)
    {
        lock (LogGate)
        {
            var path = LogPathFor(dbPath);
            if (!File.Exists(path)) return;
            var lines = ReadLog(path);
            var key = id.ToString();
            var header = lines.Count > 0 ? lines[0] : "";
            int removed = lines.RemoveAll(l => l != header && FirstField(l) == key);
            if (removed > 0) WriteLog(path, lines);
        }
    }

    private static string FlatCell(string? v)
    {
        v ??= "";
        if (v.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            v = v.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        return CsvUtil.Esc(v);
    }

    private static string FirstField(string line)
    {
        if (string.IsNullOrEmpty(line)) return "";
        int comma = line.IndexOf(',');
        return comma < 0 ? line : line[..comma];
    }

    private static List<string> ReadLog(string path)
    {
        var header = string.Join(",", Headers.Select(CsvUtil.Esc));
        if (!File.Exists(path)) return new List<string> { header };
        var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || lines[0] != header) lines.Insert(0, header);
        return lines;
    }

    private static void WriteLog(string path, List<string> lines)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(true));
        File.Move(tmp, path, overwrite: true);
    }

    public static void ExportXlsx(string path, IEnumerable<MaintenanceRecord> records)
    {
        const int S_TEXT = 0, S_HEADER = 1;
        double[] widths = { 6, 24, 14, 22, 30, 10, 10, 12, 30, 24, 20, 20 };

        var sh = new FctShared.Xlsx.Sheet { Name = "维修记录", FreezeRows = 1 };
        sh.ColWidths.AddRange(widths);

        var head = new List<FctShared.Xlsx.Cell>();
        foreach (var h in Headers) head.Add(FctShared.Xlsx.T(h, S_HEADER));
        sh.Rows.Add(head);

        foreach (var m in records)
        {
            var vals = RowOf(m);
            var row = new List<FctShared.Xlsx.Cell>();
            for (int c = 0; c < vals.Length; c++)
            {
                if (c == 0 && int.TryParse(vals[c], out var num))
                    row.Add(FctShared.Xlsx.N(num, S_TEXT));
                else
                    row.Add(FctShared.Xlsx.T(vals[c], S_TEXT));
            }
            sh.Rows.Add(row);
        }

        FctShared.Xlsx.Write(path, new[] { sh }, FctShared.Xlsx.TableStyles());
    }

}
