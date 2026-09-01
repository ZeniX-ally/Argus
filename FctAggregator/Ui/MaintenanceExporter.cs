using System.Text;

namespace FctAggregator;

/// <summary>
/// 维修记录导出: CSV(UTF-8 BOM) 和 零依赖 XLSX(标准 OOXML)。
/// 列: ID/故障项目/设备型号/设备SN/故障描述/严重度/状态/维修人/维修措施/备注/创建时间/更新时间。
/// 状态与严重度的中文名统一取自 MaintenanceMeta(单一来源), 不在此处重复定义字典。
/// </summary>
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

    // ==================== CSV ====================
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

    /// <summary>
    /// CSV 字段转义。⚠ CWE-1236 公式注入防护: Excel/WPS 会把 = + - @ 开头的单元格当公式执行,
    /// 维修措施/备注/故障描述是自由文本(可能来自 XML 或手工录入), 首字符命中时加单引号前缀。
    /// </summary>
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





    // 样式 0=普通(左对齐,边框), 1=表头(白粗,蓝底,居中,边框)
    /// <summary>
    /// 导出 xlsx。v2.9.3：打包外壳改用共享的 <see cref="FctShared.Xlsx"/>
    /// （原来这里自己写了一遍 zip/Content_Types/sheet XML，与三个工具重复），
    /// 样式与列宽仍是本导出自己的。
    ///
    /// ⚠ 与 v2.9.2 的**唯一**长相差异：<c>FreezeRows = 1</c>（冻结表头）是 v2.9.3 新增的，
    /// 旧版没有冻结窗格。12 列多行滚动时看不见表头，属改进，故保留；
    /// 2026-07-31 用 <c>tools\xlsx_check.ps1 -Compare</c> 做过新旧逐单元格对比，
    /// 除此一处外调色板 / 列宽 / 每个单元格的值与样式号完全一致。
    /// 另外三个导出口（分发 / FAIL 排行 / 取数）比对结果是**一字节不差**。
    /// </summary>
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
                // ID 列写成数值，其余写文本（与合并前一致）
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
