using System.Text;
using Cell = FctShared.Xlsx.Cell;
using Sheet = FctShared.Xlsx.Sheet;

namespace FctFetcher;

/// <summary>
/// 取数打包工具的 xlsx 样式与门面。
///
/// v2.9.3：OOXML 打包外壳（zip / Content_Types / workbook / sheet XML / 转义 /
/// 列宽 / 冻结行 / 合并单元格）已抽到共享的 <see cref="FctShared.Xlsx"/>，四处共用一份；
/// 这里只留**本工具自己的 4 种样式**与 T/N/H 便捷方法，报表长相不变。
/// <c>Cell</c> / <c>Sheet</c> 现在是 <see cref="FctShared.Xlsx"/> 的类型（using 别名）。
/// </summary>
public static class XlsxWriter
{
    public const int S_NORMAL = 0;   // 左对齐 + 细边框
    public const int S_HEADER = 1;   // 白粗字 + 蓝底 + 居中
    public const int S_TEXT_C = 2;   // 文本居中
    public const int S_NUM_C = 3;    // 数值居中

    public static Cell T(string? s, int style = S_NORMAL) => FctShared.Xlsx.T(s, style);
    public static Cell N(double n, int style = S_NUM_C) => FctShared.Xlsx.N(n, style);
    public static Cell H(string s) => FctShared.Xlsx.T(s, S_HEADER);

    /// <summary>写出 xlsx；表头默认冻结（与合并前行为一致）。</summary>
    public static void Write(string path, List<Sheet> sheets)
    {
        foreach (var sh in sheets)
            if (sh.FreezeRows == 0 && sh.Rows.Count > 0) sh.FreezeRows = 1;
        FctShared.Xlsx.Write(path, sheets, Styles());
    }

    /// <summary>自检用：把样式部件暴露出来做对拍。</summary>
    internal static string Styles2() => Styles();

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
}
