using System.IO.Compression;
using System.Text;

namespace FctShared;

/// <summary>
/// 共享的 OOXML (.xlsx) 写出器（v2.9.3）。
///
/// <b>为什么只共享"打包"不共享"样式"</b>：合并前有 4 份手写 xlsx 代码
/// （数据分发 / 取数打包 / FAIL 排行 / 维修记录导出），重复的其实是**外壳**——
/// zip 打包、`[Content_Types].xml`、`_rels`、`workbook.xml`、sheet XML 拼装、
/// 字符转义、列宽、冻结行、合并单元格。
/// 而 <c>styles.xml</c> 四家各不相同（分别是 7 / 4 / 17 / 3 种样式，配色也不一样），
/// 硬统一意味着改几百处样式号、且报表长相会变。所以调色板由调用方以
/// <see cref="Sheet"/>+<c>stylesXml</c> 形式传进来：**外观一字节不变，外壳只留一份**。
///
/// 用法：
/// <code>
/// var sh = new Xlsx.Sheet { Name = "明细", FreezeRows = 1 };
/// sh.ColWidths.AddRange(new[] { 30.0, 12.0 });
/// sh.Rows.Add(new List&lt;Xlsx.Cell&gt; { Xlsx.T("项目", MyStyles.S_HEADER), Xlsx.N(3.14, MyStyles.S_NUM) });
/// Xlsx.Write(path, new[] { sh }, MyStyles.Xml);
/// </code>
/// </summary>
public static class Xlsx
{
    /// <summary>一个单元格。<see cref="Number"/> 有值就写成数字，否则写文本（inlineStr）。</summary>
    public sealed class Cell
    {
        public string? Text;
        public double? Number;
        public int Style;

        // 与各工具原有写法兼容（原来各自的 Cell.T / Cell.N 调用点不用改）
        public static Cell T(string? s, int style = 0) => new() { Text = s ?? "", Style = style };
        public static Cell N(double n, int style = 0) => new() { Number = n, Style = style };
        public static Cell Empty(int style = 0) => new() { Text = "", Style = style };
    }

    /// <summary>一张表。行是"单元格列表"的列表；<see cref="Merges"/> 用 0 基坐标。</summary>
    public sealed class Sheet
    {
        public string Name = "Sheet1";
        public List<List<Cell>> Rows = new();
        /// <summary>列宽（字符数）；不足的列用默认宽。</summary>
        public List<double> ColWidths = new();
        /// <summary>冻结前 N 行（0 = 不冻结）。</summary>
        public int FreezeRows = 0;
        /// <summary>合并单元格 (r1,c1,r2,c2)，0 基、含端点。</summary>
        public List<(int r1, int c1, int r2, int c2)> Merges = new();

        public void AddRow(params Cell[] cells) => Rows.Add(cells.ToList());

        /// <summary>新建一行并返回它（与各工具原有的 <c>sheet.NewRow()</c> 写法兼容）。</summary>
        public List<Cell> NewRow()
        {
            var r = new List<Cell>();
            Rows.Add(r);
            return r;
        }
    }

    public static Cell T(string? s, int style = 0) => new() { Text = s ?? "", Style = style };
    public static Cell N(double n, int style = 0) => new() { Number = n, Style = style };
    public static Cell Empty(int style = 0) => new() { Text = "", Style = style };

    /// <summary>写出 xlsx。<paramref name="stylesXml"/> 是完整的 <c>&lt;styleSheet&gt;</c> 部件内容。</summary>
    public static void Write(string path, IReadOnlyList<Sheet> sheets, string stylesXml)
    {
        if (sheets == null || sheets.Count == 0)
            throw new ArgumentException("至少要有一张表", nameof(sheets));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(path)) File.Delete(path);

        using var fs = new FileStream(path, FileMode.CreateNew);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
        Add(zip, "_rels/.rels", RootRels());
        Add(zip, "xl/workbook.xml", Workbook(sheets));
        Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Count));
        Add(zip, "xl/styles.xml", stylesXml);
        for (int i = 0; i < sheets.Count; i++)
            Add(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i]));
    }

    /// <summary>XML 文本转义（&amp; &lt; &gt; " ' 以及 XML 不允许的控制字符）。</summary>
    public static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 8);
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
                    // XML 1.0 不允许的控制字符直接丢，否则 Excel 报"文件已损坏"
                    if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') break;
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>0 基列号 → Excel 列名（0→A, 26→AA）。</summary>
    public static string ColName(int index)
    {
        var sb = new StringBuilder();
        index++;
        while (index > 0)
        {
            var m = (index - 1) % 26;
            sb.Insert(0, (char)('A' + m));
            index = (index - 1) / 26;
        }
        return sb.ToString();
    }

    /// <summary>工作表名合法化：去掉 Excel 不允许的字符并截到 31 字。</summary>
    public static string SafeSheetName(string? name)
    {
        var s = (name ?? "Sheet1").Trim();
        foreach (var bad in new[] { '\\', '/', '?', '*', '[', ']', ':' }) s = s.Replace(bad, '_');
        if (s.Length == 0) s = "Sheet1";
        return s.Length > 31 ? s[..31] : s;
    }

    // ==================== 部件拼装 ====================

    private static void Add(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    private const string Decl = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    private static string ContentTypes(int n)
    {
        var sb = new StringBuilder(Decl);
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
        Decl +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private static string Workbook(IReadOnlyList<Sheet> sheets)
    {
        var sb = new StringBuilder(Decl);
        sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
        sb.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        for (int i = 0; i < sheets.Count; i++)
            sb.Append($"<sheet name=\"{Escape(SafeSheetName(sheets[i].Name))}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
        sb.Append("</sheets></workbook>");
        return sb.ToString();
    }

    private static string WorkbookRels(int n)
    {
        var sb = new StringBuilder(Decl);
        sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (int i = 1; i <= n; i++)
            sb.Append($"<Relationship Id=\"rId{i}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i}.xml\"/>");
        sb.Append($"<Relationship Id=\"rId{n + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    private static string SheetXml(Sheet sh)
    {
        var sb = new StringBuilder(Decl);
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

        if (sh.FreezeRows > 0)
            sb.Append($"<sheetViews><sheetView workbookViewId=\"0\">" +
                      $"<pane ySplit=\"{sh.FreezeRows}\" topLeftCell=\"A{sh.FreezeRows + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/>" +
                      $"</sheetView></sheetViews>");

        if (sh.ColWidths.Count > 0)
        {
            sb.Append("<cols>");
            for (int i = 0; i < sh.ColWidths.Count; i++)
                sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{sh.ColWidths[i].ToString(System.Globalization.CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
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
                var refName = $"{ColName(c)}{r + 1}";
                if (cell.Number.HasValue && double.IsFinite(cell.Number.Value))
                    sb.Append($"<c r=\"{refName}\" s=\"{cell.Style}\"><v>" +
                              cell.Number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                              "</v></c>");
                else if (!string.IsNullOrEmpty(cell.Text))
                    sb.Append($"<c r=\"{refName}\" s=\"{cell.Style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">" +
                              Escape(cell.Text) + "</t></is></c>");
                else
                    sb.Append($"<c r=\"{refName}\" s=\"{cell.Style}\"/>");
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");

        if (sh.Merges.Count > 0)
        {
            sb.Append($"<mergeCells count=\"{sh.Merges.Count}\">");
            foreach (var (r1, c1, r2, c2) in sh.Merges)
                sb.Append($"<mergeCell ref=\"{ColName(c1)}{r1 + 1}:{ColName(c2)}{r2 + 1}\"/>");
            sb.Append("</mergeCells>");
        }

        sb.Append("</worksheet>");
        return sb.ToString();
    }
}
