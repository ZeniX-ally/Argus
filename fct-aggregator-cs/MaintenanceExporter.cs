using System.IO.Compression;
using System.Text;

namespace FctAggregator;

/// <summary>
/// 维修记录导出: CSV(UTF-8 BOM) 和 零依赖 XLSX(标准 OOXML)。
/// 列: ID/故障项目/设备型号/设备SN/故障描述/严重度/状态/维修人/维修措施/备注/创建时间。
/// </summary>
public static class MaintenanceExporter
{
    private static readonly string[] Headers =
    {
        "ID", "故障项目", "设备型号", "设备SN", "故障描述",
        "严重度", "状态", "维修人", "维修措施", "备注", "创建时间",
    };

    private static readonly Dictionary<string, string> StatusZh = new()
    {
        ["open"] = "待处理", ["in_progress"] = "进行中", ["resolved"] = "已解决", ["closed"] = "已关闭",
    };
    private static readonly Dictionary<string, string> SeverityZh = new()
    {
        ["critical"] = "严重", ["major"] = "一般", ["minor"] = "轻微",
    };

    private static string[] RowOf(MaintenanceRecord m) => new[]
    {
        m.Id.ToString(),
        m.FailItem,
        m.EquipmentModel,
        m.EquipmentSn,
        m.FailReason,
        SeverityZh.GetValueOrDefault(m.Severity, m.Severity),
        StatusZh.GetValueOrDefault(m.Status, m.Status),
        m.Resolver,
        m.Resolution,
        m.Notes,
        m.CreatedAt,
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

    private static string Esc(string? v)
    {
        v ??= "";
        if (v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r'))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }

    // ==================== XLSX(零依赖 OOXML) ====================
    public static void ExportXlsx(string path, IEnumerable<MaintenanceRecord> records)
    {
        var rows = records.Select(RowOf).ToList();
        if (File.Exists(path)) File.Delete(path);
        using var fs = new FileStream(path, FileMode.CreateNew);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        AddEntry(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "</Types>");

        AddEntry(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>");

        AddEntry(zip, "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"维修记录\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");

        AddEntry(zip, "xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "</Relationships>");

        AddEntry(zip, "xl/styles.xml", Styles());
        AddEntry(zip, "xl/worksheets/sheet1.xml", SheetXml(rows));
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    // 样式 0=普通(左对齐,边框), 1=表头(白粗,蓝底,居中,边框)
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

    private static string SheetXml(List<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

        // 列宽
        double[] widths = { 6, 24, 14, 22, 30, 10, 10, 12, 30, 24, 20 };
        sb.Append("<cols>");
        for (int c = 0; c < widths.Length; c++)
            sb.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{widths[c].ToString(System.Globalization.CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
        sb.Append("</cols>");

        sb.Append("<sheetData>");
        // 表头行
        sb.Append("<row r=\"1\">");
        for (int c = 0; c < Headers.Length; c++)
            sb.Append($"<c r=\"{ColName(c)}1\" s=\"1\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{XmlEsc(Headers[c])}</t></is></c>");
        sb.Append("</row>");
        // 数据行
        for (int i = 0; i < rows.Count; i++)
        {
            int rowNum = i + 2;
            sb.Append($"<row r=\"{rowNum}\">");
            var row = rows[i];
            for (int c = 0; c < row.Length; c++)
            {
                var refStr = $"{ColName(c)}{rowNum}";
                // ID 列写为数值, 其余写文本
                if (c == 0 && int.TryParse(row[c], out var num))
                    sb.Append($"<c r=\"{refStr}\" s=\"0\"><v>{num}</v></c>");
                else
                    sb.Append($"<c r=\"{refStr}\" s=\"0\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{XmlEsc(row[c])}</t></is></c>");
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static string ColName(int index)
    {
        var sb = new StringBuilder();
        index++;
        while (index > 0)
        {
            int rem = (index - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            index = (index - 1) / 26;
        }
        return sb.ToString();
    }

    private static string XmlEsc(string? s)
    {
        s ??= "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
