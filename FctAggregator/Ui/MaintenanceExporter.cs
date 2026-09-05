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

        FctShared.Xlsx.Write(path, new[] { sh }, Styles());
    }

    private static string Styles() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<fonts count=\"2\">" +
        "<font><sz val=\"11\"/><name val=\"微软雅黑\"/></font>" +
        "<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"微软雅黑\"/></font>" +
        "</fonts>" +
        "<fills count=\"3\">" +
        "<fill><patternFill patternType=\"none\"/></fill>" +
        "<fill><patternFill patternType=\"gray125\"/></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF4472C4\"/></patternFill></fill>" +
        "</fills>" +
        "<borders count=\"2\">" +
        "<border><left/><right/><top/><bottom/><diagonal/></border>" +
        "<border>" +
        "<left style=\"thin\"><color rgb=\"FFBFBFBF\"/></left><right style=\"thin\"><color rgb=\"FFBFBFBF\"/></right>" +
        "<top style=\"thin\"><color rgb=\"FFBFBFBF\"/></top><bottom style=\"thin\"><color rgb=\"FFBFBFBF\"/></bottom>" +
        "<diagonal/></border>" +
        "</borders>" +
        "<cellStyleXfs count=\"1\"><xf/></cellStyleXfs>" +
        "<cellXfs count=\"2\">" +
        "<xf fontId=\"0\" fillId=\"0\" borderId=\"1\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\">" +
        "<alignment horizontal=\"left\" vertical=\"center\" wrapText=\"1\"/></xf>" +
        "<xf fontId=\"1\" fillId=\"2\" borderId=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\">" +
        "<alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
        "</cellXfs>" +
        "</styleSheet>";

}
