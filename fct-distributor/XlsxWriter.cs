using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace FctDistributor;

/// <summary>
/// 零依赖 .xlsx 生成器（标准 OOXML，只用 System.IO.Compression）。
///
/// 只做本工具需要的事：表头 + 表格数据 + 列宽 + 冻结首行。
/// 第一行是表头、第二行起是数据，方便直接复制粘贴到别的系统里。
/// </summary>
public static class XlsxWriter
{
    // ---- 样式索引（与 Styles() 里 cellXfs 顺序一一对应）----
    public const int S_TEXT   = 0;   // 文本（左对齐，细边框）
    public const int S_HEADER = 1;   // 表头（粗、白字、蓝底、居中）
    public const int S_CENTER = 2;   // 文本居中
    public const int S_NUM    = 3;   // 数值居中
    public const int S_ZEBRA  = 4;   // 斑马纹（左对齐）
    public const int S_ZEBRA_C = 5;  // 斑马纹（居中）
    public const int S_TOTAL  = 6;   // 合计行（粗、浅蓝底、居中）

    public sealed class Cell
    {
        public string? Text;
        public double? Number;
        public int Style = S_TEXT;
        public static Cell T(string? s, int style = S_TEXT) => new() { Text = s ?? "", Style = style };
        public static Cell N(double n, int style = S_NUM) => new() { Number = n, Style = style };
    }

    public sealed class Row
    {
        public List<Cell> Cells = new();
        public Row Add(Cell c) { Cells.Add(c); return this; }
    }

    public sealed class Sheet
    {
        public string Name = "Sheet1";
        public List<Row> Rows = new();
        public List<double> ColWidths = new();
        /// <summary>冻结前 N 行（0 = 不冻结）。</summary>
        public int FreezeRows = 1;

        public Row NewRow() { var r = new Row(); Rows.Add(r); return r; }

        public void AddHeader(params (string Title, double Width)[] cols)
        {
            var r = NewRow();
            foreach (var (t, w) in cols) { r.Add(Cell.T(t, S_HEADER)); ColWidths.Add(w); }
        }
    }

    public static void Write(string path, IReadOnlyList<Sheet> sheets)
    {
        if (sheets.Count == 0) throw new ArgumentException("没有任何 Sheet");
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(path)) File.Delete(path);

        using var fs = new FileStream(path, FileMode.CreateNew);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        AddEntry(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
        AddEntry(zip, "_rels/.rels", RootRels());
        AddEntry(zip, "xl/workbook.xml", Workbook(sheets));
        AddEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Count));
        AddEntry(zip, "xl/styles.xml", Styles());
        for (int i = 0; i < sheets.Count; i++)
            AddEntry(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i]));
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    private static string ContentTypes(int n)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (int i = 1; i <= n; i++)
            sb.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        sb.Append("</Types>");
        return sb.ToString();
    }

    private static string RootRels() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private static string Workbook(IReadOnlyList<Sheet> sheets)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
        sb.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        for (int i = 0; i < sheets.Count; i++)
            sb.Append($"<sheet name=\"{XmlEsc(sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
        sb.Append("</sheets></workbook>");
        return sb.ToString();
    }

    private static string WorkbookRels(int n)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (int i = 1; i <= n; i++)
            sb.Append($"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
        sb.Append($"<Relationship Id=\"rId{n + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    private static string Styles()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

        sb.Append("<fonts count=\"4\">");
        sb.Append("<font><sz val=\"11\"/><name val=\"微软雅黑\"/></font>");                                    // 0 普通
        sb.Append("<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"微软雅黑\"/></font>");        // 1 表头白粗
        sb.Append("<font><b/><sz val=\"11\"/><color rgb=\"FF1F4E79\"/><name val=\"微软雅黑\"/></font>");        // 2 深蓝粗
        sb.Append("<font><sz val=\"10\"/><name val=\"Consolas\"/></font>");                                    // 3 等宽（SN 用）
        sb.Append("</fonts>");

        sb.Append("<fills count=\"5\">");
        sb.Append("<fill><patternFill patternType=\"none\"/></fill>");                                          // 0
        sb.Append("<fill><patternFill patternType=\"gray125\"/></fill>");                                       // 1 占位
        sb.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF4472C4\"/></patternFill></fill>");   // 2 表头蓝
        sb.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF7F9FC\"/></patternFill></fill>");   // 3 斑马极浅
        sb.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFD9E1F2\"/></patternFill></fill>");   // 4 合计浅蓝
        sb.Append("</fills>");

        sb.Append("<borders count=\"2\">");
        sb.Append("<border><left/><right/><top/><bottom/><diagonal/></border>");
        sb.Append("<border>");
        sb.Append("<left style=\"thin\"><color rgb=\"FFBFBFBF\"/></left>");
        sb.Append("<right style=\"thin\"><color rgb=\"FFBFBFBF\"/></right>");
        sb.Append("<top style=\"thin\"><color rgb=\"FFBFBFBF\"/></top>");
        sb.Append("<bottom style=\"thin\"><color rgb=\"FFBFBFBF\"/></bottom>");
        sb.Append("<diagonal/></border>");
        sb.Append("</borders>");

        sb.Append("<cellStyleXfs count=\"1\"><xf/></cellStyleXfs>");
        sb.Append("<cellXfs count=\"7\">");
        Xf(sb, font: 0, fill: 0, border: 1, hAlign: "left");     // 0 S_TEXT
        Xf(sb, font: 1, fill: 2, border: 1, hAlign: "center");   // 1 S_HEADER
        Xf(sb, font: 0, fill: 0, border: 1, hAlign: "center");   // 2 S_CENTER
        Xf(sb, font: 0, fill: 0, border: 1, hAlign: "center");   // 3 S_NUM
        Xf(sb, font: 0, fill: 3, border: 1, hAlign: "left");     // 4 S_ZEBRA
        Xf(sb, font: 0, fill: 3, border: 1, hAlign: "center");   // 5 S_ZEBRA_C
        Xf(sb, font: 2, fill: 4, border: 1, hAlign: "center");   // 6 S_TOTAL
        sb.Append("</cellXfs>");
        sb.Append("<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");

        sb.Append("</styleSheet>");
        return sb.ToString();
    }

    private static void Xf(StringBuilder sb, int font, int fill, int border, string hAlign)
    {
        sb.Append($"<xf fontId=\"{font}\" fillId=\"{fill}\" borderId=\"{border}\" ");
        sb.Append("applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\">");
        sb.Append($"<alignment horizontal=\"{hAlign}\" vertical=\"center\" wrapText=\"0\"/>");
        sb.Append("</xf>");
    }

    private static string SheetXml(Sheet sheet)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

        // 冻结表头
        if (sheet.FreezeRows > 0)
        {
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\">");
            sb.Append($"<pane ySplit=\"{sheet.FreezeRows}\" topLeftCell=\"A{sheet.FreezeRows + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/>");
            sb.Append("<selection pane=\"bottomLeft\"/>");
            sb.Append("</sheetView></sheetViews>");
        }

        if (sheet.ColWidths.Count > 0)
        {
            sb.Append("<cols>");
            for (int c = 0; c < sheet.ColWidths.Count; c++)
                sb.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{sheet.ColWidths[c].ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
            sb.Append("</cols>");
        }

        sb.Append("<sheetData>");
        for (int r = 0; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            sb.Append($"<row r=\"{r + 1}\">");
            for (int c = 0; c < row.Cells.Count; c++)
            {
                var cell = row.Cells[c];
                var refStr = $"{ColName(c)}{r + 1}";
                if (cell.Number.HasValue)
                    sb.Append($"<c r=\"{refStr}\" s=\"{cell.Style}\"><v>{cell.Number.Value.ToString(CultureInfo.InvariantCulture)}</v></c>");
                else
                    sb.Append($"<c r=\"{refStr}\" s=\"{cell.Style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{XmlEsc(cell.Text)}</t></is></c>");
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    public static string ColName(int index)
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
        var sb = new StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default:
                    // XML 1.0 非法控制字符直接丢掉，避免 Excel 报“文件已损坏”
                    if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') break;
                    sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }
}
