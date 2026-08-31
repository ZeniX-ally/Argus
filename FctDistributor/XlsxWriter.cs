using System.Text;
using Cell = FctShared.Xlsx.Cell;
using Sheet = FctShared.Xlsx.Sheet;

namespace FctDistributor;

/// <summary>
/// 数据分发工具的 xlsx 样式与门面。
///
/// v2.9.3：OOXML 打包外壳已抽到共享的 <see cref="FctShared.Xlsx"/>（四处共用一份），
/// 这里只留本工具自己的 7 种样式与便捷方法，报表长相不变。
/// </summary>
public static class XlsxWriter
{
    public const int S_TEXT   = 0;   // 文本（左对齐，细边框）
    public const int S_HEADER = 1;   // 表头（粗、白字、蓝底、居中）
    public const int S_CENTER = 2;   // 文本居中
    public const int S_NUM    = 3;   // 数值居中
    public const int S_ZEBRA  = 4;   // 斑马纹（左对齐）
    public const int S_ZEBRA_C = 5;  // 斑马纹（居中）
    public const int S_TOTAL  = 6;   // 合计行（粗、浅蓝底、居中）

    public static Cell T(string? s, int style = S_TEXT) => FctShared.Xlsx.T(s, style);
    public static Cell N(double n, int style = S_NUM) => FctShared.Xlsx.N(n, style);

    /// <summary>加一行表头并按给定宽度设置列宽（原 Sheet.AddHeader，改成扩展方法）。</summary>
    public static void AddHeader(this Sheet sh, params (string Title, double Width)[] cols)
    {
        var row = new List<Cell>();
        foreach (var (title, width) in cols)
        {
            row.Add(T(title, S_HEADER));
            sh.ColWidths.Add(width);
        }
        sh.Rows.Add(row);
    }

    /// <summary>写出 xlsx；默认冻结首行（与合并前行为一致）。</summary>
    public static void Write(string path, IReadOnlyList<Sheet> sheets)
    {
        foreach (var sh in sheets)
            if (sh.FreezeRows == 0 && sh.Rows.Count > 0) sh.FreezeRows = 1;
        FctShared.Xlsx.Write(path, sheets, Styles());
    }

    /// <summary>0 基列号 → Excel 列名（对外保留，原来有调用方用它）。</summary>
    public static string ColName(int index) => FctShared.Xlsx.ColName(index);

    /// <summary>自检用：把样式部件暴露出来做对拍。</summary>
    internal static string Styles2() => Styles();

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
}
