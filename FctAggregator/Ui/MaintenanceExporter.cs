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
        sb.AppendLine(string.Join(",", Headers.Select(Esc)));
        foreach (var m in records)
            sb.AppendLine(string.Join(",", RowOf(m).Select(Esc)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string Esc(string? v)
    {
        v ??= "";
        if (v.Length > 0 && (v[0] == '=' || v[0] == '+' || v[0] == '-' || v[0] == '@' ||
                             v[0] == '\t' || v[0] == '\r'))
            v = "'" + v;
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r'))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
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
