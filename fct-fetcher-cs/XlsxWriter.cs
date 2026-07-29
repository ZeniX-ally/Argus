using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace FctFetcher;

/// <summary>
/// 零依赖生成 .xlsx (标准 OOXML)，与 fct-fail-ranker/XlsxExporter 同一套骨架，
/// 但只保留本工具需要的 4 种样式，避免无谓复杂度。
/// </summary>
public static class XlsxWriter
{
    public const int S_NORMAL = 0;   // 左对齐 + 细边框
    public const int S_HEADER = 1;   // 白粗字 + 蓝底 + 居中
    public const int S_TEXT_C = 2;   // 文本居中
    public const int S_NUM_C = 3;    // 数值居中

    public sealed class Cell
    {
        public string Text = "";
        public double? Number;
        public int Style = S_NORMAL;
    }

    public sealed class Sheet
    {
        public string Name = "Sheet1";
        public List<List<Cell>> Rows = new();
        public List<double> ColWidths = new();
        public bool FreezeHeader = true;
    }

    public static Cell T(string? s, int style = S_NORMAL) => new() { Text = s ?? "", Style = style };
    public static Cell N(double n, int style = S_NUM_C) => new() { Number = n, Style = style };
    public static Cell H(string s) => new() { Text = s, Style = S_HEADER };

    public static void Write(string path, List<Sheet> sheets)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(path)) File.Delete(path);

        using var fs = new FileStream(path, FileMode.CreateNew);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
        Add(zip, "_rels/.rels", RootRels());
        Add(zip, "xl/workbook.xml", Workbook(sheets));
        Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Count));
        Add(zip, "xl/styles.xml", Styles());
        for (int i = 0; i < sheets.Count; i++)
            Add(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i]));
    }

    private static void Add(ZipArchive zip, string name, string content)
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

    private static string Workbook(List<Sheet> sheets)
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

        sb.Append("<fonts count=\"2\">");
        sb.Append("<font><sz val=\"11\"/><name val=\"微软雅黑\"/></font>");
        sb.Append("<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"微软雅黑\"/></font>");
        sb.Append("</fonts>");

        sb.Append("<fills count=\"3\">");
        sb.Append("<fill><patternFill patternType=\"none\"/></fill>");
        sb.Append("<fill><patternFill patternType=\"gray125\"/></fill>");   // Excel 约定占位
        sb.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF4472C4\"/></patternFill></fill>");
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
        sb.Append("<cellXfs count=\"4\">");
        Xf(sb, 0, 0, 1, "left");     // 0 NORMAL
        Xf(sb, 1, 2, 1, "center");   // 1 HEADER
        Xf(sb, 0, 0, 1, "center");   // 2 TEXT_C
        Xf(sb, 0, 0, 1, "center");   // 3 NUM_C
        sb.Append("</cellXfs>");
        // 缺了 cellStyles 会让部分读取器(如 openpyxl)警告 "no default style"
        sb.Append("<cellStyles count=\"1\">");
        sb.Append("<cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/>");
        sb.Append("</cellStyles>");
        sb.Append("</styleSheet>");
        return sb.ToString();
    }

    private static void Xf(StringBuilder sb, int font, int fill, int border, string hAlign)
    {
        sb.Append($"<xf fontId=\"{font}\" fillId=\"{fill}\" borderId=\"{border}\" ");
        sb.Append("applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\">");
        sb.Append($"<alignment horizontal=\"{hAlign}\" vertical=\"center\" wrapText=\"0\"/></xf>");
    }

    private static string SheetXml(Sheet sh)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

        if (sh.FreezeHeader && sh.Rows.Count > 1)
        {
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\">");
            sb.Append("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>");
            sb.Append("</sheetView></sheetViews>");
        }

        if (sh.ColWidths.Count > 0)
        {
            sb.Append("<cols>");
            for (int c = 0; c < sh.ColWidths.Count; c++)
                sb.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"" +
                          sh.ColWidths[c].ToString(CultureInfo.InvariantCulture) +
                          "\" customWidth=\"1\"/>");
            sb.Append("</cols>");
        }

        sb.Append("<sheetData>");
        for (int r = 0; r < sh.Rows.Count; r++)
        {
            var row = sh.Rows[r];
            sb.Append($"<row r=\"{r + 1}\">");
            for (int c = 0; c < row.Count; c++)
            {
                var cell = row[c];
                var refStr = $"{ColName(c)}{r + 1}";
                if (cell.Number.HasValue)
                {
                    sb.Append($"<c r=\"{refStr}\" s=\"{cell.Style}\"><v>");
                    sb.Append(cell.Number.Value.ToString(CultureInfo.InvariantCulture));
                    sb.Append("</v></c>");
                }
                else if (cell.Text.Length > 0)
                {
                    sb.Append($"<c r=\"{refStr}\" s=\"{cell.Style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">");
                    sb.Append(XmlEsc(cell.Text));
                    sb.Append("</t></is></c>");
                }
                else
                {
                    sb.Append($"<c r=\"{refStr}\" s=\"{cell.Style}\"/>");
                }
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static string ColName(int index)
    {
        var sb = new StringBuilder();
        int i = index;
        do
        {
            sb.Insert(0, (char)('A' + i % 26));
            i = i / 26 - 1;
        } while (i >= 0);
        return sb.ToString();
    }

    /// <summary>XML 转义 + 剔除 OOXML 不允许的控制字符(否则 Excel 报文件损坏)。</summary>
    private static string XmlEsc(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default:
                    if (ch == '\t' || ch == '\n' || ch == '\r' || ch >= 0x20) sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }
}
